using System.Text;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.Core;

namespace Docket.Mcp.Dashboard;

/// <summary>
/// The §12 transcript surface: an index of what is readable for a task, and the raw stream
/// itself. Kept in its own file rather than folded into <see cref="DashboardEndpoints"/>
/// because its auth rule is deliberately different from every other dashboard route (see
/// <see cref="RequireHumanAsync"/>), and that difference should be impossible to miss.
///
/// <para><b>Transcripts are served verbatim.</b> Docket does not redact them — how to do it
/// well is unresolved (§13, §16 open question 8) — so the compensating controls are all
/// here and in <see cref="TranscriptRelayService"/>: a human operator session only, a
/// terminal task only, a warning the operator cannot miss, and a response the plane never
/// stores or logs.</para>
///
/// <para>The raw stream is served as <c>text/plain</c> and the HTML page only links to it.
/// Transcript bytes are attacker-influenced content — an agent prints what it reads — so
/// they are never interpolated into the dashboard's HTML, and no escaping bug can turn a
/// transcript into script.</para>
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

    /// <summary>
    /// The warning that leads every served transcript. In the body, not only in HTML chrome,
    /// so it survives a copy-paste, a <c>curl</c>, or a saved file — the places an operator
    /// is most likely to forget what they are holding.
    /// </summary>
    internal const string Warning =
        "[docket] Raw harness output, served verbatim. It may contain credentials, customer " +
        "data, or anything else the agent read or printed. Docket does not redact transcripts " +
        "(spec §13, open question 8). Treat this text as sensitive: do not paste it into a " +
        "ticket, a chat, or another agent.";

    public static IEndpointRouteBuilder MapDashboardTranscripts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard/sessions/{sessionId}/transcripts", HandleIndexAsync);
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
    /// The index: which dispatches this task had, which machine each ran on, and what that
    /// machine still holds. One inventory request per connected machine (§12).
    /// </summary>
    private static async Task<IResult> HandleIndexAsync(
        string sessionId, HttpContext http, TokenService tokens, DashboardQueries queries,
        TranscriptRelayService relay, TimeProvider clock, CancellationToken ct)
    {
        if (await RequireHumanAsync(http, tokens, ct) is { } refusal)
            return refusal;
        if (!Guid.TryParse(sessionId, out var id))
            return Results.BadRequest(new { error = "invalid session id" });

        var locations = await queries.GetTranscriptLocationsAsync(id, ct);
        var task = new SessionId(id);

        // Ask each distinct connected machine once, even if it ran several attempts: the
        // inventory is per task, not per attempt, and the ordinals come back with it.
        var inventories = new List<TranscriptMachineInventory>();
        foreach (var machine in locations
                     .Where(l => l is { Connected: true, Machine: not null })
                     .Select(l => l.Machine!)
                     .Distinct(StringComparer.Ordinal))
        {
            inventories.Add(await relay.ListAsync(task, machine, ct) switch
            {
                TranscriptResult.Inventory inv => new TranscriptMachineInventory(machine, inv.Instances, null),
                TranscriptResult.Unavailable u => new TranscriptMachineInventory(machine, [], u.Detail),
                _ => new TranscriptMachineInventory(machine, [], "Unexpected reply."),
            });
        }

        return Results.Content(
            DashboardTranscriptRenderer.Index(task, locations, inventories, clock.GetUtcNow()),
            "text/html; charset=utf-8");
    }

    /// <summary>
    /// The raw stream: follow the machine's cursor, writing each range straight to the
    /// response. Nothing is buffered beyond one range and nothing is persisted (§12).
    /// </summary>
    private static async Task<IResult> HandleStreamAsync(
        string sessionId, HttpContext http, TokenService tokens, TranscriptRelayService relay,
        TimeProvider clock, CancellationToken ct)
    {
        if (await RequireHumanAsync(http, tokens, ct) is { } refusal)
            return refusal;
        if (!Guid.TryParse(sessionId, out var id))
            return Results.BadRequest(new { error = "invalid session id" });

        var machine = http.Request.Query["machine"].ToString();
        var stream = http.Request.Query["stream"].ToString() is { Length: > 0 } s ? s : TranscriptStreams.Stdout;
        if (string.IsNullOrWhiteSpace(machine))
            return Results.BadRequest(new { error = "machine is required" });
        if (!int.TryParse(http.Request.Query["ordinal"].ToString(), out var ordinal) || ordinal < 1)
            return Results.BadRequest(new { error = "ordinal must be a positive instance number" });
        if (!TranscriptStreams.IsKnown(stream))
            return Results.BadRequest(new { error = "stream must be stdout or stderr" });

        var task = new SessionId(id);

        // The first range decides the status code, so it is fetched before any byte of the
        // response is committed: an unreadable transcript must be an HTTP error, not a 200
        // whose body happens to explain a failure.
        var first = await relay.ReadAsync(task, machine, ordinal, stream, offset: 0, RangeBytes, ct);
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
        await WriteAsync(http, $"[docket] task {task} · machine {machine} · instance {ordinal:D4} · {stream}\n\n");
        await WriteAsync(http, range.Text);

        var deadline = clock.GetUtcNow() + StreamDeadline;
        var offset = range.NextOffset;
        var eof = range.Eof;
        while (!eof && !ct.IsCancellationRequested)
        {
            if (clock.GetUtcNow() > deadline)
            {
                // In-band: the status line is long gone, so say so where the reader will see
                // it — the same honesty as the capture side's truncation marker (§12).
                await WriteAsync(http, $"\n[docket] transcript stream interrupted: exceeded the " +
                                       $"{StreamDeadline.TotalMinutes:N0}-minute read deadline at offset {offset}.\n");
                break;
            }

            var next = await relay.ReadAsync(task, machine, ordinal, stream, offset, RangeBytes, ct);
            if (next is not TranscriptResult.Range more)
            {
                var detail = next is TranscriptResult.Unavailable u ? u.Detail : "unexpected reply";
                await WriteAsync(http, $"\n[docket] transcript stream interrupted at offset {offset}: {detail}\n");
                break;
            }

            await WriteAsync(http, more.Text);
            // A range that returns nothing and does not advance would spin; the reader
            // guarantees progress while bytes remain, so treat a stalled cursor as the end.
            if (more.NextOffset <= offset && !more.Eof)
            {
                await WriteAsync(http, $"\n[docket] transcript stream ended early at offset {offset}.\n");
                break;
            }
            offset = more.NextOffset;
            eof = more.Eof;
        }

        return Results.Empty;
    }

    private static async Task WriteAsync(HttpContext http, string text)
    {
        // Write and flush per range so a large transcript streams rather than buffering, and
        // so the operator's connection is what paces the next request to the machine (§12).
        await http.Response.WriteAsync(text, Encoding.UTF8);
        await http.Response.Body.FlushAsync();
    }

    /// <summary>
    /// The one dashboard route family that refuses a Lead token. Every other §12 view is
    /// reachable with one, deliberately — §12 requires a structured twin a reattaching Lead
    /// can consume. But a Lead token lives in an agent's MCP client, and a full transcript is
    /// unbounded untrusted text carrying every byte the agent read: orders of magnitude past
    /// the 16 KB cap `report_result` enforces precisely to bound that surface, and
    /// unredacted. So "human-only" is enforced on the credential here rather than assumed
    /// from the dashboard gate (§2 principle 3 — enforcement where a model cannot reason past
    /// it). Extending transcripts to agents is future work and needs the fenced-untrusted
    /// treatment plus a hard size bound (§12).
    /// </summary>
    private static async Task<IResult?> RequireHumanAsync(
        HttpContext http, TokenService tokens, CancellationToken ct) =>
        await DashboardAuth.ResolveAsync(http, tokens, ct) switch
        {
            Principal.Human => null,
            Principal.Lead => Results.Content(
                DashboardTranscriptRenderer.LeadRefused(), "text/html; charset=utf-8",
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Redirect("/dashboard/login"),
        };

    private static IResult Unavailable(HttpContext http, TranscriptResult.Unavailable unavailable)
    {
        var status = unavailable.Reason switch
        {
            TranscriptUnavailable.NoSuchSession => StatusCodes.Status404NotFound,
            // Not an error in the task's world — the transcript is simply not readable yet.
            TranscriptUnavailable.NotTerminal => StatusCodes.Status409Conflict,
            TranscriptUnavailable.Busy => StatusCodes.Status409Conflict,
            TranscriptUnavailable.MachineRefused => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        return Results.Content(
            DashboardTranscriptRenderer.Unavailable(unavailable),
            "text/html; charset=utf-8", statusCode: status);
    }
}

/// <summary>What one machine reports holding for a task, or why it could not say (§12).</summary>
public sealed record TranscriptMachineInventory(
    string Machine, IReadOnlyList<TranscriptInstance> Instances, string? Unavailable);
