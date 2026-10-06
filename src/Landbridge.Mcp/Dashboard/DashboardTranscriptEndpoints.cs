using System.Text;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// The §12 transcript surface: an index of what is readable for a task, and the raw stream
/// itself. Kept in its own file rather than folded into <see cref="DashboardEndpoints"/>
/// because its auth rule is deliberately different from every other dashboard route (see
/// <see cref="RequireHumanAsync"/>), and that difference should be impossible to miss.
///
/// <para><b>Transcripts are served verbatim, including while the session is running.</b>
/// Landbridge does not redact them (§13). Harnesses and models are responsible for not
/// printing secrets. This page is a human operator session; a Lead reads one bounded range
/// with <c>read_transcript</c>. The response is never stored or logged.</para>
///
/// <para>The raw stream is served as <c>text/plain</c> and the HTML page only links to it.
/// Transcript bytes are attacker-influenced content — an agent prints what it reads — so
/// they are never interpolated into the dashboard's HTML, and no escaping bug can turn a
/// transcript into script. <c>follow=1</c> keeps reading past a caught-up end while the
/// session is still running; without it the response is a snapshot.</para>
/// </summary>
public static class DashboardTranscriptEndpoints
{
    /// <summary>How many bytes to ask a machine for per range. The plane asks for the next
    /// range only after this one has been written to the operator's connection, so this is
    /// also the most transcript data the plane ever holds at once (§12).</summary>
    private const int RangeBytes = TranscriptStreams.DefaultMaxBytes;

    /// <summary>
    /// A hard stop on one response, so a wedged machine or a stalled browser cannot hold a
    /// runner-channel read slot forever (the relay is single-flight per machine).
    /// </summary>
    private static readonly TimeSpan StreamDeadline = TimeSpan.FromMinutes(5);

    /// <summary>How long a live tail waits after the file is caught up before asking again.
    /// The per-machine semaphore is not held across this wait.</summary>
    private static readonly TimeSpan FollowPoll = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The warning that leads every served transcript. In the body, not only in HTML chrome,
    /// so it survives a copy-paste, a <c>curl</c>, or a saved file — the places an operator
    /// is most likely to forget what they are holding.
    /// </summary>
    internal const string Warning =
        "[landbridge] Raw harness output, served verbatim. It may contain credentials, customer " +
        "data, or anything else the agent read or printed. Landbridge does not redact transcripts. " +
        "Treat this text as sensitive.";

    public static IEndpointRouteBuilder MapDashboardTranscripts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard/sessions/{sessionId}/transcript", HandleStreamAsync);
        app.MapGet("/dashboard/tasks/{sessionId}/transcripts",
            (string sessionId) => Results.Redirect($"/dashboard/sessions/{sessionId}/transcripts", permanent: true));
        app.MapGet("/dashboard/tasks/{sessionId}/transcript", (string sessionId, HttpContext http) =>
        {
            var qs = http.Request.QueryString.HasValue ? http.Request.QueryString.Value : "";
            return Results.Redirect($"/dashboard/sessions/{sessionId}/transcript{qs}", permanent: true);
        });
        return app;
    }

    /// <summary>
    /// The raw stream: follow the machine's cursor, writing each range straight to the
    /// response. Nothing is buffered beyond one range and nothing is persisted (§12).
    /// The HTML index is the Blazor <c>Transcripts</c> page. <c>follow=1</c> keeps asking
    /// after the file is caught up, until the session is terminal (one confirmation read,
    /// so a cancel's wind-down is not missed) or the deadline.
    /// </summary>
    private static async Task<IResult> HandleStreamAsync(
        string sessionId, HttpContext http, TokenService tokens, TranscriptRelayService relay,
        SessionStore store, TimeProvider clock, CancellationToken ct)
    {
        if (await RequireHumanAsync(http, tokens, ct) is { } refusal)
            return refusal;
        var queries = http.RequestServices.GetRequiredService<DashboardQueries>();
        var resolved = await queries.ResolveSessionAsync(sessionId, ct);
        if (resolved.Malformed)
            return Results.BadRequest(new { error = "invalid session id" });
        if (resolved.Id is not { } id)
            return Results.NotFound(new { error = "no such session" });

        var machineText = http.Request.Query["machine"].ToString();
        var stream = http.Request.Query["stream"].ToString() is { Length: > 0 } s ? s : TranscriptStreams.Stdout;
        if (string.IsNullOrWhiteSpace(machineText))
            return Results.BadRequest(new { error = "machine is required" });
        if (!Guid.TryParse(machineText, out var machine))
            return Results.BadRequest(new { error = "machine must be a machine id" });
        if (!int.TryParse(http.Request.Query["ordinal"].ToString(), out var ordinal) || ordinal < 1)
            return Results.BadRequest(new { error = "ordinal must be a positive instance number" });
        if (!TranscriptStreams.IsKnown(stream))
            return Results.BadRequest(new { error = "stream must be stdout or stderr" });

        var task = new SessionId(id);
        var core = http.RequestServices.GetService<CoreWriteClient>();
        var bearer = DashboardAuth.ReadToken(http);
        var follow = WantsFollow(http.Request);

        Task<TranscriptResult> ReadRange(long off) =>
            TranscriptAccess.ReadAsync(relay, core, bearer, task, machine, ordinal, stream, off, RangeBytes, ct);

        // The first range decides the status code, so it is fetched before any byte of the
        // response is committed: an unreadable transcript must be an HTTP error, not a 200
        // whose body happens to explain a failure.
        var first = await ReadRange(0);
        if (first is TranscriptResult.Unavailable unavailable)
            return Unavailable(http, unavailable);
        if (first is not TranscriptResult.Range range)
            return Results.StatusCode(StatusCodes.Status502BadGateway);

        // Verbatim, sensitive, and never cached anywhere: no-store for the browser, nosniff
        // so it cannot be re-interpreted as HTML, and nothing written plane-side.
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/plain; charset=utf-8";
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";

        await WriteAsync(http, Warning + "\n");
        await WriteAsync(http, $"[landbridge] task {task} · machine {machine} · instance {ordinal:D4} · {stream}\n\n");
        await WriteAsync(http, range.Text);

        var deadline = clock.GetUtcNow() + StreamDeadline;
        var offset = range.NextOffset;
        var eof = range.Eof;
        // Set once a caught-up read has been confirmed after the session went terminal.
        // A cancel is marked before wind-down finishes writing, so one eof is not enough.
        var confirmed = false;
        while (!ct.IsCancellationRequested)
        {
            if (clock.GetUtcNow() > deadline)
            {
                // In-band: the status line is long gone, so say so where the reader will see
                // it — the same honesty as the capture side's truncation marker (§12).
                await WriteAsync(http, $"\n[landbridge] transcript stream interrupted: exceeded the " +
                                       $"{StreamDeadline.TotalMinutes:N0}-minute read deadline at offset {offset}.\n");
                break;
            }

            if (eof)
            {
                if (!follow)
                    break;
                var state = await store.GetStateAsync(task, ct);
                var terminal = state is null || state.Value.IsTerminal();
                if (terminal)
                {
                    if (confirmed)
                        break;
                    confirmed = true;
                }
                else
                {
                    confirmed = false;
                    try
                    {
                        await Task.Delay(FollowPoll, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            var next = await ReadRange(offset);
            if (next is not TranscriptResult.Range more)
            {
                var detail = next is TranscriptResult.Unavailable u ? u.Detail : "unexpected reply";
                await WriteAsync(http, $"\n[landbridge] transcript stream interrupted at offset {offset}: {detail}\n");
                break;
            }

            await WriteAsync(http, more.Text);
            // A range that returns nothing and does not advance would spin; the reader
            // guarantees progress while bytes remain, so treat a stalled cursor as the end.
            if (more.NextOffset <= offset && !more.Eof)
            {
                await WriteAsync(http, $"\n[landbridge] transcript stream ended early at offset {offset}.\n");
                break;
            }
            if (more.NextOffset > offset)
                confirmed = false;
            offset = more.NextOffset;
            eof = more.Eof;
        }

        return Results.Empty;
    }

    private static bool WantsFollow(HttpRequest request)
    {
        var value = request.Query["follow"].ToString();
        return value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteAsync(HttpContext http, string text)
    {
        // Write and flush per range so a large transcript streams rather than buffering, and
        // so the operator's connection is what paces the next request to the machine (§12).
        await http.Response.WriteAsync(text, Encoding.UTF8);
        await http.Response.Body.FlushAsync();
    }

    /// <summary>
    /// The dashboard HTML and this stream refuse a Lead token. Every other §12 view is
    /// reachable with one, deliberately. A Lead reads one bounded range with
    /// <c>read_transcript</c> — this response is a multi-minute tail, which an MCP call
    /// must not hold. The credential check is here rather than assumed from the dashboard
    /// gate (§2 principle 3).
    /// </summary>
    private static async Task<IResult?> RequireHumanAsync(
        HttpContext http, TokenService tokens, CancellationToken ct) =>
        await DashboardAuth.ResolveAsync(http, tokens, ct) switch
        {
            Principal.Human => null,
            Principal.Lead => DashboardHosting.RazorPage<Components.Pages.TranscriptLeadRefusedPage>(
                status: StatusCodes.Status403Forbidden),
            _ => Results.Redirect("/dashboard/login"),
        };

    private static IResult Unavailable(HttpContext http, TranscriptResult.Unavailable unavailable)
    {
        var status = unavailable.Reason switch
        {
            TranscriptUnavailable.NoSuchSession => StatusCodes.Status404NotFound,
            // Retained. A live session is readable; a caller that still names this is a conflict.
            TranscriptUnavailable.NotTerminal => StatusCodes.Status409Conflict,
            TranscriptUnavailable.Busy => StatusCodes.Status409Conflict,
            TranscriptUnavailable.MachineRefused => StatusCodes.Status404NotFound,
            // Not "try again later": this machine does not serve transcripts, and will not
            // start because the caller retried. The remaining reasons — offline, timeout —
            // are the ones a retry can actually clear.
            TranscriptUnavailable.NotServable => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        return DashboardHosting.RazorPage<Components.Pages.TranscriptUnavailablePage>(
            new { Unavailable = unavailable }, status);
    }
}

/// <summary>What one machine reports holding for a task, or why it could not say (§12).</summary>
public sealed record TranscriptMachineInventory(
    Guid Machine, IReadOnlyList<TranscriptInstance> Instances, string? Unavailable);
