using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Landbridge.Mcp;

/// <summary>
/// Passthrough to the internal hub. Copies the inbound Bearer; Hub owns
/// read authorization. Disabled when <c>Landbridge:HubUrl</c> is unset
/// (test hosts that do not start Hub).
/// </summary>
public sealed class HubClient(HttpClient http, ILogger<HubClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public bool Enabled => http.BaseAddress is not null;

    /// <summary>
    /// The hub's answer, or <c>default</c> when there is no answer to be had.
    /// A caller that falls back to a direct query cannot act on the difference
    /// between "hub says no rows" and "hub never replied", so both come back as
    /// <c>default</c> — but every non-success is logged, because the one thing
    /// worse than a degraded read is a silent one.
    /// </summary>
    public async Task<T?> GetAsync<T>(string path, string bearer, CancellationToken ct)
    {
        if (!Enabled)
            return default;
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadFromJsonAsync<T>(Json, ct);

            // 404 is an ordinary answer: the entity is not there, or not there
            // for this bearer (Hub answers 404 rather than 403 where existence
            // is itself privileged). Anything else is worth a line in the log.
            if (resp.StatusCode is HttpStatusCode.NotFound)
                return default;

            // 401/403 means the bearer this host copied through is not good
            // enough for the hub. That is a wiring fault, not an empty result,
            // and it would otherwise render as a blank page.
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                logger.LogWarning(
                    "hub GET {Path} refused the forwarded bearer: {Status}", path, (int)resp.StatusCode);
            else
                logger.LogWarning("hub GET {Path} failed: {Status}", path, (int)resp.StatusCode);
            return default;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "hub GET {Path} did not complete", path);
            return default;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "hub GET {Path} timed out", path);
            return default;
        }
    }

    /// <summary>
    /// The inbound Bearer, or null. Dashboard cookies are copied onto this
    /// header before the host talks to Hub.
    /// </summary>
    public static string? BearerOf(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header is { Length: > 0 } && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    /// <summary>
    /// Membership SSE. Reconnects with <c>Last-Event-ID</c>. Catch-up on first
    /// open is coalesced by the caller; this just yields <c>event: change</c>.
    /// </summary>
    public async IAsyncEnumerable<HubChange> WatchAsync(
        string path, string bearer, [EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<HubChange>();
        var pump = PumpAsync(ch.Writer, path, bearer, ct);
        try
        {
            await foreach (var change in ch.Reader.ReadAllAsync(ct))
                yield return change;
        }
        finally
        {
            try { await pump; }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Live SSE for inbox watch. Skips <c>hub_queue</c> catch-up (the snapshot
    /// GET is complete). The first yielded value is <see cref="HubChange.Live"/>
    /// once Hub has subscribed, so the caller snapshots after the waiter is up.
    /// </summary>
    public async IAsyncEnumerable<HubChange> WatchLiveAsync(
        string path, string bearer, [EnumeratorCancellation] CancellationToken ct)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ch = Channel.CreateUnbounded<HubChange>();
        var pump = PumpAsync(ch.Writer, WithAfter(path, long.MaxValue), bearer, ct, opened);
        try
        {
            await opened.Task.WaitAsync(ct);
            yield return HubChange.Live;
            await foreach (var change in ch.Reader.ReadAllAsync(ct))
                yield return change;
        }
        finally
        {
            try { await pump; }
            catch (OperationCanceledException) { }
        }
    }

    internal static string WithAfter(string path, long after)
    {
        var sep = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{path}{sep}after={after}";
    }

    private async Task PumpAsync(
        ChannelWriter<HubChange> writer, string path, string bearer, CancellationToken ct,
        TaskCompletionSource? opened = null)
    {
        long last = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stop = false;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, path);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    if (last > 0)
                        req.Headers.TryAddWithoutValidation("Last-Event-ID", last.ToString());
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        logger.LogWarning("hub SSE {Path} refused the forwarded bearer: {Status}", path, (int)resp.StatusCode);
                        opened?.TrySetException(new HttpRequestException(
                            $"hub SSE {path} refused the forwarded bearer: {(int)resp.StatusCode}"));
                        return;
                    }
                    if (resp.StatusCode is HttpStatusCode.NotFound)
                    {
                        opened?.TrySetException(new HttpRequestException($"hub SSE {path} not found"));
                        return;
                    }
                    resp.EnsureSuccessStatusCode();
                    opened?.TrySetResult();
                    opened = null;
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var reader = new StreamReader(stream);
                    await foreach (var frame in ReadSseAsync(reader, ct))
                    {
                        if (frame.Event is not "change" || string.IsNullOrEmpty(frame.Data))
                            continue;
                        if (frame.Id is not null && long.TryParse(frame.Id, out var id) && id > last)
                            last = id;
                        HubChange? change;
                        try
                        {
                            change = JsonSerializer.Deserialize<HubChange>(frame.Data, Json);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }
                        if (change is not null)
                            await writer.WriteAsync(change, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    opened?.TrySetCanceled(ct);
                    return;
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning(ex, "hub SSE {Path} dropped", path);
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "hub SSE {Path} dropped", path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "hub SSE {Path} failed", path);
                    opened?.TrySetException(ex);
                    stop = true;
                }

                if (stop || ct.IsCancellationRequested)
                    return;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        finally
        {
            opened?.TrySetCanceled(ct);
            writer.TryComplete();
        }
    }

    private static async IAsyncEnumerable<(string? Id, string? Event, string Data)> ReadSseAsync(
        StreamReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        string? id = null, ev = null;
        var data = new System.Text.StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                yield break;
            if (line.Length == 0)
            {
                if (ev is not null || data.Length > 0 || id is not null)
                {
                    yield return (id, ev, data.ToString());
                    id = ev = null;
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith(":", StringComparison.Ordinal))
                continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case "id":
                    id = value;
                    break;
                case "event":
                    ev = value;
                    break;
                case "data":
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(value);
                    break;
            }
        }
    }
}

public sealed record HubChange(
    [property: JsonPropertyName("queueId")] long QueueId,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("entityId")] Guid? EntityId)
{
    /// <summary>
    /// WatchLive: Hub has subscribed. Not a <c>hub_queue</c> row.
    /// </summary>
    public static HubChange Live { get; } = new(0, "live", null);

    public bool IsLiveOpen => QueueId == 0 && Topic == "live";
}


