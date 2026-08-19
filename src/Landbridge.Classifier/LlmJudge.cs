using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Landbridge.Classifier;

public sealed class LlmJudge(ClassifierSettings settings, ILogger<LlmJudge> log) : ILlmJudge
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Total budget for Lead messages in the judge user payload.</summary>
    internal const int MessagesCapBytes = 24 * 1024;

    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);

    public async Task<ClassifyResponse> JudgeAsync(
        string tool, JsonElement? input, string? command,
        IReadOnlyList<string>? messages, CancellationToken ct)
    {
        try
        {
            var user = BuildUserMessage(tool, input, command, messages);

            var stage1 = await ChatJsonAsync(settings.Fast, user, ct).ConfigureAwait(false);
            if (stage1.ShouldBlock == false)
                return ClassifyResult.Allow("classifier-fast");

            // Explicit true *and* a parse miss (null / garbage JSON) go to
            // review. The fast prompt errs toward blocking so stage 2 can
            // still allow a false positive; a missing shouldBlock is the
            // same kind of uncertain block, not an outage.
            if (stage1.ShouldBlock is null)
                log.LogWarning("classifier fast did not return shouldBlock; reviewing");

            var stage2 = await ChatJsonAsync(settings.Review, user, ct).ConfigureAwait(false);
            return CombineStages(stage1, stage2);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "classifier llm failed");
            return ClassifyResult.Ask("classifier-unavailable");
        }
    }

    private async Task<JudgeJson> ChatJsonAsync(JudgeStage stage, string user, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(stage.TimeoutMs);

        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        if (!stage.Slug.IsGpt5Family)
        {
            options.Temperature = 0;
            options.MaxOutputTokens = stage.MaxTokens;
        }

        var response = await ClientFor(stage)
            .GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, stage.Prompt),
                    new ChatMessage(ChatRole.User, user),
                ],
                options,
                timeout.Token)
            .ConfigureAwait(false);

        return ParseJudgeJson(response.Text);
    }

    /// <summary>
    /// Stage 1 only short-circuits on an explicit allow. A true or missing
    /// <c>shouldBlock</c> is reviewed; a missing stage-2 decision is Ask.
    /// </summary>
    internal static ClassifyResponse CombineStages(JudgeJson stage1, JudgeJson stage2)
    {
        if (stage1.ShouldBlock == false)
            return ClassifyResult.Allow("classifier-fast");
        if (stage2.ShouldBlock == false)
            return ClassifyResult.Allow("classifier-review");
        if (stage2.ShouldBlock == true)
            return ClassifyResult.Ask("classifier-block", Sanitize(stage2.Reason));
        return ClassifyResult.Ask("classifier-unavailable");
    }

    /// <summary>
    /// Empty, unparseable, or field-less JSON is a missing decision — not an
    /// exception. Stage 1 treats that as an uncertain block and still reviews.
    /// </summary>
    internal static JudgeJson ParseJudgeJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new(null, null);
        try
        {
            return JsonSerializer.Deserialize<JudgeJson>(content, Json) ?? new(null, null);
        }
        catch (JsonException)
        {
            return new(null, null);
        }
    }

    private IChatClient ClientFor(JudgeStage stage) =>
        _clients.GetOrAdd(
            stage.Slug.Wire,
            model => ChatClients.Create(settings.LiteLlm.Url, settings.LiteLlm.ApiKey, model));

    internal static string BuildUserMessage(
        string tool, JsonElement? input, string? command, IReadOnlyList<string>? messages)
    {
        var sb = new StringBuilder();
        sb.Append("LEAD MESSAGES TO THE WORKER (task brief, in order):\n");
        var used = 0;
        var any = false;
        if (messages is not null)
        {
            foreach (var raw in messages)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var text = raw.Trim();
                if (used >= MessagesCapBytes)
                    break;
                if (used + text.Length > MessagesCapBytes)
                    text = text[..(MessagesCapBytes - used)];
                if (any)
                    sb.Append("\n---\n");
                sb.Append(text);
                used += text.Length;
                any = true;
            }
        }
        if (!any)
            sb.Append("(none)");
        sb.Append("\n\nUNTRUSTED TOOL REQUEST DATA (JSON):\n");
        sb.Append(JsonSerializer.Serialize(new { tool, command, input }, Json));
        return sb.ToString();
    }

    internal static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        var stripped = raw;
        for (var i = 0; i < 8; i++)
        {
            var next = Regex.Replace(stripped, "<[^>]*>", "");
            if (next == stripped)
                break;
            stripped = next;
        }
        var flat = Regex.Replace(stripped, @"\s+", " ").Trim();
        return flat.Length <= 200 ? flat : flat[..200];
    }

    internal sealed record JudgeJson(bool? ShouldBlock, string? Reason);
}
