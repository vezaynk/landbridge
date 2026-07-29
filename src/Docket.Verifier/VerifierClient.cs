using System.Net;
using System.Net.Http.Json;

namespace Docket.Verifier;

/// <summary>How a poll of GET <c>/verify/pending</c> resolved.</summary>
public enum PollStatus
{
    /// <summary>200: the list (possibly empty) is in hand.</summary>
    Ok,

    /// <summary>401/403: the credential is not a live verifier (§5). Misprovisioned.</summary>
    Unauthorized,

    /// <summary>Network error, timeout, or any non-auth non-2xx — retry next tick.</summary>
    Unreachable,
}

/// <summary>How a POST of a verdict resolved (§6, §10).</summary>
public enum VerdictStatus
{
    /// <summary>200: applied; <see cref="VerdictResult.Detail"/> is the new state.</summary>
    Applied,

    /// <summary>409: someone else ruled, or the task already left <c>verifying</c>. Benign.</summary>
    Conflict,

    /// <summary>404: the task is gone. Benign.</summary>
    NotFound,

    /// <summary>400: the plane rejected the request shape. Should not happen; log and skip.</summary>
    BadRequest,

    /// <summary>401/403: the credential was refused mid-tick (e.g. revoked).</summary>
    Unauthorized,

    /// <summary>Network error, timeout, or any other non-2xx — retry next tick.</summary>
    Unreachable,
}

/// <summary>The outcome of a poll: a status plus the tasks (empty unless <see cref="PollStatus.Ok"/>).</summary>
public readonly record struct PollResult(PollStatus Status, IReadOnlyList<PendingTask> Tasks, string? Detail);

/// <summary>The outcome of a verdict POST: a status plus a human-readable detail (state or reason).</summary>
public readonly record struct VerdictResult(VerdictStatus Status, string? Detail);

/// <summary>
/// The verifier's entire outbound surface (spec §10 — "the verifier posts to
/// Docket, not the reverse"): poll the pending list, post a verdict. A thin
/// wrapper over an <see cref="HttpClient"/> whose <c>BaseAddress</c> is the plane
/// and whose default Authorization header carries the verifier bearer token.
///
/// <para>Every transport failure is turned into a status rather than an
/// exception, so a single tick never throws on a hostile network or a moved
/// task — the loop stays alive and retries on the next interval. Only
/// <see cref="OperationCanceledException"/> from the caller's shutdown token
/// propagates, which is how the loop exits cleanly on SIGINT/SIGTERM.</para>
/// </summary>
public sealed class VerifierClient(HttpClient http)
{
    public async Task<PollResult> GetPendingAsync(CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync("/verify/pending", ct);
        }
        catch (HttpRequestException e)
        {
            return new PollResult(PollStatus.Unreachable, [], e.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed — a slow/unreachable plane, not our shutdown.
            return new PollResult(PollStatus.Unreachable, [], "request timed out");
        }

        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new PollResult(PollStatus.Unauthorized, [], $"HTTP {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                return new PollResult(PollStatus.Unreachable, [], $"HTTP {(int)resp.StatusCode}");

            var tasks = await resp.Content.ReadFromJsonAsync(
                VerifierJsonContext.Default.ListPendingTask, ct) ?? [];
            return new PollResult(PollStatus.Ok, tasks, null);
        }
    }

    public async Task<VerdictResult> PostVerdictAsync(Guid taskId, string verdict, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync(
                $"/verify/{taskId}", new VerdictBody(verdict), VerifierJsonContext.Default.VerdictBody, ct);
        }
        catch (HttpRequestException e)
        {
            return new VerdictResult(VerdictStatus.Unreachable, e.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VerdictResult(VerdictStatus.Unreachable, "request timed out");
        }

        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    var ok = await ReadBodyAsync(resp, ct);
                    return new VerdictResult(VerdictStatus.Applied, ok?.State);
                case HttpStatusCode.Conflict:
                    var conflict = await ReadBodyAsync(resp, ct);
                    // 409 is either a rule rejection (rule+reason) or an
                    // optimistic-concurrency conflict (reason only); surface whichever.
                    return new VerdictResult(VerdictStatus.Conflict,
                        conflict?.Rule is { } rule ? $"{rule}: {conflict.Reason}" : conflict?.Reason);
                case HttpStatusCode.NotFound:
                    var notFound = await ReadBodyAsync(resp, ct);
                    return new VerdictResult(VerdictStatus.NotFound, notFound?.Reason);
                case HttpStatusCode.BadRequest:
                    var bad = await ReadBodyAsync(resp, ct);
                    return new VerdictResult(VerdictStatus.BadRequest, bad?.Reason);
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return new VerdictResult(VerdictStatus.Unauthorized, $"HTTP {(int)resp.StatusCode}");
                default:
                    return new VerdictResult(VerdictStatus.Unreachable, $"HTTP {(int)resp.StatusCode}");
            }
        }
    }

    private static async Task<VerdictResponseBody?> ReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync(VerifierJsonContext.Default.VerdictResponseBody, ct);
        }
        catch (System.Text.Json.JsonException)
        {
            // A well-behaved plane always sends a JSON body on these codes; if one
            // ever doesn't, the status still drove the decision above, so treat the
            // missing detail as absent rather than failing the whole tick.
            return null;
        }
    }
}
