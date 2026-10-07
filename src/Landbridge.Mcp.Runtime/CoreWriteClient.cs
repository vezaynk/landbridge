using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.ControlPlane;
using Landbridge.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Landbridge.Mcp;

/// <summary>
/// Façade → Core writes. Same Bearer as the inbound call. When
/// <c>Landbridge:CoreUrl</c> is unset (tests), callers keep using
/// <see cref="SessionStore"/> in-process.
/// </summary>
public sealed class CoreWriteClient(HttpClient http, ILogger<CoreWriteClient> logger)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public bool Enabled => http.BaseAddress is not null;

    public async Task<CoreStoreReply> PostAsync<T>(
        string path, string bearer, T body, CancellationToken ct, string? prefer = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (!string.IsNullOrWhiteSpace(prefer))
            req.Headers.TryAddWithoutValidation("Prefer", prefer);
        using var resp = await http.SendAsync(req, ct);
        CoreStoreReply? reply;
        try
        {
            reply = await resp.Content.ReadFromJsonAsync<CoreStoreReply>(Json, ct);
        }
        catch (JsonException)
        {
            reply = null;
        }
        reply ??= new CoreStoreReply("conflict", Reason: $"core {path} returned {(int)resp.StatusCode} with no body");
        if (!resp.IsSuccessStatusCode && reply.Status == "applied")
            reply = reply with { Status = "conflict", Reason = $"core {path} HTTP {(int)resp.StatusCode}" };
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("core POST {Path} {Status}: {Reason}", path, (int)resp.StatusCode, reply.Reason);
        return reply;
    }

    public async Task<T?> PostAsAsync<T>(
        string path, string bearer, object body, CancellationToken ct, string? prefer = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (!string.IsNullOrWhiteSpace(prefer))
            req.Headers.TryAddWithoutValidation("Prefer", prefer);
        using var resp = await http.SendAsync(req, ct);
        T? parsed = default;
        try
        {
            parsed = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (JsonException)
        {
        }
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("core POST {Path} {Status}", path, (int)resp.StatusCode);
        return parsed;
    }

    /// <summary>
    /// A façade → Core read. The body is not logged: transcript ranges are verbatim
    /// harness output and must not land in a log line.
    /// </summary>
    public async Task<T?> GetAsync<T>(string path, string bearer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await http.SendAsync(req, ct);
        try
        {
            return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (JsonException)
        {
            logger.LogWarning("core GET {Path} {Status} was not json", path, (int)resp.StatusCode);
            return default;
        }
    }
}

public sealed record CoreStoreReply(
    string Status,
    string? State = null,
    string? SessionId = null,
    string? Slug = null,
    string? Rule = null,
    string? Reason = null,
    string? Detail = null,
    string? CommandId = null)
{
    public static CoreStoreReply From(StoreResult result) => result switch
    {
        StoreResult.Applied a => new(
            "applied",
            a.Session.State.ToString(),
            a.Session.Id.Value.ToString("D"),
            Reason: $"ok: session is now {a.Session.State}"),
        StoreResult.Rejected r => new("rejected", Rule: r.Rule.ToString(), Reason: r.Reason),
        StoreResult.NotFound n => new("not_found", Reason: n.Reason),
        StoreResult.Conflict c => new("conflict", Reason: c.Reason),
        _ => new("conflict", Reason: "unknown store result"),
    };

    public static CoreStoreReply FromCommand(CommandRow row) => row.Status switch
    {
        CommandRow.Applied => new(
            "applied",
            State: row.Reason,
            SessionId: row.SessionId.ToString("D"),
            Slug: row.Slug,
            Reason: row.Slug ?? row.Reason,
            CommandId: row.Id.ToString("D")),
        CommandRow.Rejected => new(
            "rejected", Rule: row.Rule, Reason: row.Reason, CommandId: row.Id.ToString("D")),
        _ => new(
            "accepted",
            SessionId: row.SessionId.ToString("D"),
            CommandId: row.Id.ToString("D"),
            Reason: $"accepted: command {row.Id:D} session {row.SessionId:D}"),
    };

    public string Describe() => Status switch
    {
        "applied" => Reason ?? "ok",
        "accepted" => Reason ?? $"accepted: command {CommandId} session {SessionId}",
        "rejected" => throw new McpException($"rejected ({Rule}): {Reason}"),
        "not_found" => throw new McpException(Reason ?? "not found"),
        _ => throw new McpException($"conflict: {Reason}"),
    };
}

public sealed record CoreCreateSessionBody(string TeamId, string Description, string Profile, string? SessionId = null);
public sealed record CoreConformanceSpec(string Kind, string Description);
public sealed record CoreConformanceBody(string Profile, IReadOnlyList<CoreConformanceSpec> Sessions, Guid? RunId = null);
public sealed record CoreConformanceSession(Guid SessionId, string Kind, string State, int Attempt);
public sealed record CoreConformanceReply(bool Ok, Guid? RunId, IReadOnlyList<CoreConformanceSession>? Sessions, string? Reason);
public sealed record CoreSessionBody(string TeamId, int? TtlSeconds = null, string? Answer = null, string? Text = null, string? Option = null, string? Message = null, string? ResultReference = null, string? Report = null, string? Kind = null, string? Name = null, int? Port = null);

/// <summary>
/// One decision inside <c>answer_permission_requests</c>. The Lead still picks each
/// harness option; the batch only stops those picks being one round-trip apiece.
/// </summary>
public sealed record PermissionAnswer(string SessionId, string Option, string? Message = null);

public sealed record CorePermissionBatchBody(string TeamId, IReadOnlyList<PermissionAnswer> Decisions);

/// <summary>
/// What one entry of a permission batch did. A refusal is a row in this list, not an
/// exception, so a later decision still applies.
/// </summary>
public sealed record PermissionAnswerResult(
    string SessionId,
    string Status,
    string? State = null,
    string? Rule = null,
    string? Reason = null)
{
    public static PermissionAnswerResult? Blank(PermissionAnswer decision)
    {
        if (string.IsNullOrWhiteSpace(decision.SessionId))
            return new("", "rejected", Reason: "sessionId is required");
        if (string.IsNullOrWhiteSpace(decision.Option))
            return new(decision.SessionId, "rejected",
                Rule: Landbridge.Core.Rule.PermissionOptionMustBeOffered.ToString(),
                Reason: "option is required: pick an optionId from get_lead_inbox, or 'allow'/'deny'.");
        return null;
    }

    public static PermissionAnswerResult Missing(string sessionId) =>
        new(sessionId, "not_found", Reason: $"'{sessionId}' is not a valid session id.");

    public static PermissionAnswerResult From(string sessionId, StoreResult result) => result switch
    {
        StoreResult.Applied a => new(sessionId, "applied", a.Session.State.ToString(),
            Reason: $"ok: session is now {a.Session.State}"),
        StoreResult.Rejected r => new(sessionId, "rejected", Rule: r.Rule.ToString(), Reason: r.Reason),
        StoreResult.NotFound n => new(sessionId, "not_found", Reason: n.Reason),
        StoreResult.Conflict c => new(sessionId, "conflict", Reason: c.Reason),
        _ => new(sessionId, "conflict", Reason: "unknown store result"),
    };

    public static PermissionAnswerResult From(string sessionId, CoreStoreReply reply) =>
        new(sessionId, reply.Status, reply.State, reply.Rule, reply.Reason);
}
public sealed record CoreBindBody(string MachineId);
public sealed record CoreProcessStartBody(string Name, string[] Spawn, string? WorkingDirectory, Dictionary<string, string>? Env, bool OpenStdin);
public sealed record CoreProcessBody(string Name, string? Data = null, bool AppendNewline = true);
public sealed record CoreProcessStartReply(bool Started, string? LogPath, string? Refusal);
public sealed record CoreProcessActionReply(bool Ok, string? Refusal, int? Value);
public sealed record CoreForwardBody(string ServiceName, string? TeamId = null);
public sealed record CoreForwardReply(bool Ok, string? Host, int? Port, string? ForwardId, DateTimeOffset? ExpiresAt, string? Reason, string? Rule = null);
public sealed record CorePreviewMintBody(string ServiceName, bool IsPublic = false, int? TtlMinutes = null, string? TeamId = null, string? SessionId = null);
public sealed record CorePreviewReply(bool Ok, string? Url = null, string? Auth = null, DateTimeOffset? ExpiresAt = null, Guid? PreviewId = null, string? Reason = null, string? Label = null);
public sealed record CorePreviewPatchBody(Guid PreviewId, bool? IsPublic = null, bool Revoke = false);
public sealed record CoreFrictionBody(string Message, string? TeamId = null);
public sealed record CoreRevokeMachineBody(string MachineId);
public sealed record CoreRevokeMachineReply(bool Ok, bool ChannelClosed, int SessionsRequeued, int WorkersRevoked, string? Reason);
public sealed record CoreCloseForwardBody(Guid ForwardId);
public sealed record CoreCloseForwardReply(bool Ok, string? ServiceName = null, string? Reason = null);
