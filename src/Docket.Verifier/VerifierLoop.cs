namespace Docket.Verifier;

/// <summary>How one poll-and-rule tick resolved, for the loop's auth-failure accounting.</summary>
public enum TickStatus
{
    /// <summary>The poll succeeded; any verdicts it could post were posted (or benignly skipped).</summary>
    Polled,

    /// <summary>The plane was unreachable this tick; nothing was done. Retry next interval.</summary>
    Unreachable,

    /// <summary>The poll was refused by auth (401/403). Feeds the consecutive-failure exit.</summary>
    Unauthorized,
}

/// <summary>The result of one tick: its status and how many verdicts the plane actually applied.</summary>
public readonly record struct TickResult(TickStatus Status, int VerdictsApplied);

/// <summary>
/// One poll-and-rule tick (spec §5, §6, §10), factored out of <see cref="Program"/>
/// so it is directly testable against a real plane host: <see cref="Program"/>'s
/// loop is nothing but this method on a timer.
///
/// <para>Poll the pending list; for each task, decide a verdict from its
/// <c>resultReference</c> by the verifier's own policy (§7); POST it; log one
/// line per verdict. A 409 (someone else ruled, or the task already left
/// <c>verifying</c>) and a 404 (gone) are benign — logged and skipped, never
/// fatal. The auth status of the tick is decided by the <b>poll</b>, the
/// deterministic first call each tick that uses the same credential every POST
/// would; a POST refused mid-tick is logged loudly as an anomaly but does not
/// itself trip the exit counter — the next poll catches a genuinely dead
/// token.</para>
/// </summary>
public static class VerifierLoop
{
    public static async Task<TickResult> RunTickAsync(
        VerifierClient client, VerdictPolicy policy, VerifierLog log, CancellationToken ct)
    {
        var poll = await client.GetPendingAsync(ct);
        switch (poll.Status)
        {
            case PollStatus.Unauthorized:
                log.Error(
                    $"auth rejected polling /verify/pending ({poll.Detail}) — " +
                    $"is ${VerifierConfig.TokenEnvVar} a live verifier credential?");
                return new TickResult(TickStatus.Unauthorized, 0);
            case PollStatus.Unreachable:
                log.Warn($"plane unreachable ({poll.Detail}); retrying next tick");
                return new TickResult(TickStatus.Unreachable, 0);
        }

        var applied = 0;
        foreach (var task in poll.Tasks)
        {
            ct.ThrowIfCancellationRequested();
            var verdict = policy.Decide(task.ResultReference);
            var result = await client.PostVerdictAsync(task.TaskId, verdict, ct);
            if (LogVerdict(log, task, verdict, result))
                applied++;
        }

        return new TickResult(TickStatus.Polled, applied);
    }

    /// <summary>Logs one line for a posted verdict; returns whether the plane applied it.</summary>
    private static bool LogVerdict(VerifierLog log, PendingTask task, string verdict, VerdictResult result)
    {
        var reference = string.IsNullOrEmpty(task.ResultReference) ? "(none)" : task.ResultReference;
        var line = $"verdict task={task.TaskId} verdict={verdict} ref={reference}";
        switch (result.Status)
        {
            case VerdictStatus.Applied:
                log.Info($"{line} -> {result.Detail}");
                return true;
            case VerdictStatus.Conflict:
                log.Info($"{line} -> already ruled/moved ({result.Detail}); skipping");
                return false;
            case VerdictStatus.NotFound:
                log.Info($"{line} -> gone (404); skipping");
                return false;
            case VerdictStatus.BadRequest:
                log.Warn($"{line} -> rejected as malformed ({result.Detail}); skipping");
                return false;
            case VerdictStatus.Unauthorized:
                log.Error($"{line} -> auth rejected ({result.Detail}); token may have been revoked mid-tick");
                return false;
            default:
                log.Warn($"{line} -> could not be posted ({result.Detail}); retrying next tick");
                return false;
        }
    }
}
