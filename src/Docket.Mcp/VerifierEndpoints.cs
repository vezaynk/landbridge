using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Docket.Mcp.Auth;

namespace Docket.Mcp;

/// <summary>
/// The verifier webhook (spec §5, §10, §15). Plain HTTP, deliberately <b>not</b>
/// MCP: an automated verifier posts a verdict to Docket — "the verifier posts to
/// Docket, not the reverse" (§10). It is not an agent, so it gets no MCP tools;
/// these two endpoints are its entire surface.
///
/// Every handler resolves the caller from the authenticated token
/// (<see cref="DocketClaims.ToPrincipal"/>) and requires it to be a
/// <see cref="Principal.Verifier"/> — any other principal (worker, lead, machine,
/// human) is refused 403. The narrowing invariant of §5 is thereby enforced at
/// the door: only the human-provisioned verifier credential reaches the verdict
/// transitions out of <c>verifying</c>, plus the read scope (list verifying,
/// fetch result reference) needed to find them and nothing else.
///
/// A thin transport shell, exactly like <see cref="RunnerEndpoint"/>: all policy
/// lives in <see cref="TaskStore"/> and <see cref="TaskStateMachine"/>. The
/// handler only maps the store's outcome onto an HTTP status.
/// </summary>
public static class VerifierEndpoints
{
    /// <summary>The POST /verify/{taskId} body: a single verdict string (§10).</summary>
    public sealed record VerifyVerdictRequest(string? Verdict);

    public static IEndpointRouteBuilder MapVerifierEndpoints(this IEndpointRouteBuilder app)
    {
        // Grouped under /verify with .RequireAuthorization() so the auth pipeline
        // rejects an unauthenticated caller before any handler runs; the handlers
        // then narrow an authenticated non-verifier to 403 (§5).
        var verify = app.MapGroup("/verify").RequireAuthorization();
        verify.MapGet("/pending", HandlePendingAsync);
        verify.MapPost("/{taskId}", HandleVerdictAsync);
        return app;
    }

    /// <summary>
    /// GET /verify/pending → the verifier's read scope (§5): the automated tasks
    /// awaiting a verdict, each with its result reference. 200 with the list, or
    /// 403 if the caller is not the verifier credential.
    /// </summary>
    private static async Task<IResult> HandlePendingAsync(
        HttpContext http, TaskStore store, CancellationToken ct)
    {
        if (DocketClaims.ToPrincipal(http.User) is not Principal.Verifier)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return Results.Ok(await store.ListVerifyingAsync(ct));
    }

    /// <summary>
    /// POST /verify/{taskId} {"verdict":"accept"|"fail"} → the verdict transition
    /// out of <c>verifying</c> (§6). Maps the store outcome onto HTTP:
    /// <list type="bullet">
    /// <item>Applied → 200 with the new state (accept → completed; fail →
    ///   submitted while retries remain, else rejected — §6).</item>
    /// <item>Rejected → 409 with the rule and its reason. A verdict against an
    ///   already-terminal task lands here (TerminalStatesAreFinal), which is §10's
    ///   explicit "gone" response — never a silent success.</item>
    /// <item>NotFound → 404.</item>
    /// <item>Conflict (optimistic-concurrency race) → 409 with the reason.</item>
    /// </list>
    /// Only the store's own reason strings cross the wire; no prose is invented here.
    /// </summary>
    private static async Task<IResult> HandleVerdictAsync(
        string taskId, VerifyVerdictRequest? body,
        HttpContext http, TaskStore store, CancellationToken ct)
    {
        if (DocketClaims.ToPrincipal(http.User) is not Principal.Verifier verifier)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (!Guid.TryParse(taskId, out var id))
            return Results.BadRequest(new { reason = $"'{taskId}' is not a valid task id" });

        var verdict = body?.Verdict?.Trim().ToLowerInvariant();
        if (verdict is not ("accept" or "fail"))
            return Results.BadRequest(new { reason = "verdict must be 'accept' or 'fail'" });

        TaskCommand command = verdict == "accept"
            ? new VerdictAccept(verifier.Actor)
            : new VerdictFail(verifier.Actor);

        var result = await store.ApplyAsync(new TaskId(id), command, ct);
        return result switch
        {
            StoreResult.Applied a => Results.Ok(new { state = a.Task.State.ToString() }),
            StoreResult.Rejected r => Results.Json(
                new { rule = r.Rule.ToString(), reason = r.Reason }, statusCode: StatusCodes.Status409Conflict),
            StoreResult.NotFound n => Results.NotFound(new { reason = n.Reason }),
            StoreResult.Conflict c => Results.Json(
                new { reason = c.Reason }, statusCode: StatusCodes.Status409Conflict),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
