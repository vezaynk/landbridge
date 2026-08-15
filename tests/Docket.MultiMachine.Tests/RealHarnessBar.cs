using Docket.ControlPlane.Tests;
using Docket.Core;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// The portable real-harness minimum bar: one body per claim, driven by a
/// <see cref="RealHarnessProfile"/>. Each Category-tagged class wraps these so
/// CI <c>--filter Category=…</c> still isolates a job. xUnit does not apply a
/// derived class's <c>[Trait]</c> to methods declared on a base type, so the
/// wrappers are the load-bearing half of the filter — not decoration.
///
/// <para>Three claims, and only these three, because they are the ones an ACP
/// session plus <c>session/load</c> make generic. Stop delivery and mixed fleets
/// stay per-CLI.</para>
/// </summary>
internal static class RealHarnessBar
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan PerLegBudget = TimeSpan.FromMinutes(8);

    /// <summary>
    /// A real worker reads <c>get_task</c>, reports the live-description token,
    /// and drives the task to <see cref="TaskState.Verifying"/> on a two-machine
    /// fleet. The same run asserts the §11 session ref landed — without it every
    /// later resume would silently cold-start.
    /// </summary>
    public static async Task DriveToVerifyingAsync(PostgresFixture pg, RealHarnessProfile profile)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;

        await using var rig = profile.OpenEchoRig(pg);
        await rig.StartAsync(ct);
        using var attach = profile.AttachTo(rig);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        var token = RealHarnessProfiles.NewToken();
        var task = await rig.CreateTaskAsync(RealHarnessProfiles.EchoDescription("A", token), ct);

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(task, "A", MaxAttempts, PerLegBudget, ct),
            $"the real {profile.Name} worker never drove its task to verifying.\n"
            + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));

        Assert.Contains(token, await rig.ResultReferenceAsync(task, ct));
        Assert.Equal("A", rig.MachineRanOn(task));
        Assert.False(
            string.IsNullOrWhiteSpace(await rig.HarnessSessionRefAsync(task, ct)),
            $"no harness session ref was stamped for {profile.Name} — session/new did not "
            + "return a sessionId, so §11 resume would silently cold-start.\n"
            + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));
    }

    /// <summary>
    /// Usage the harness itself emitted reached the plane. Skips when the
    /// profile declares the stream carries none. Cost is asserted only where
    /// the harness computes one — Codex reports tokens and no dollars, and a
    /// derived $0.00 would be the mislabelling §2 principle 2 forbids.
    /// </summary>
    public static async Task ReportsUsageAsync(PostgresFixture pg, RealHarnessProfile profile)
    {
        Skip.If(profile.Usage == UsageExpectation.None,
            profile.Name + " declares no usage the bar can assert on");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;

        await using var rig = profile.OpenEchoRig(pg);
        await rig.StartAsync(ct);
        using var attach = profile.AttachTo(rig);
        await rig.AddMachineAsync("A");

        var token = RealHarnessProfiles.NewToken();
        var task = await rig.CreateTaskAsync(RealHarnessProfiles.EchoDescription("A", token), ct);

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(task, "A", MaxAttempts, PerLegBudget, ct),
            $"no verifying task, so no usage to assert on ({profile.Name}).\n"
            + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));

        if (profile.Usage == UsageExpectation.Cost)
        {
            Assert.True(
                await FleetRig.WaitUntilAsync(
                    async () => await rig.ReportedUsageAsync(task, ct) is [{ CostUsd: not null }, ..],
                    TimeSpan.FromMinutes(2)),
                $"no usage report with a cost reached the plane for {profile.Name}.\n"
                + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));
            var rows = await rig.ReportedUsageAsync(task, ct);
            Assert.Contains(rows, u => u.CostUsd is > 0m);
            Assert.True(
                rows.Sum(u => u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheWriteTokens) > 0,
                $"a cost landed for {profile.Name} but every token bucket was zero");
            if (profile.NamesModel)
                Assert.Contains(rows, u => !string.IsNullOrWhiteSpace(u.Model));
            return;
        }

        Assert.True(
            await FleetRig.WaitUntilAsync(
                async () => (await rig.ReportedUsageAsync(task, ct)).Count > 0,
                TimeSpan.FromMinutes(2)),
            $"no usage report reached the plane for {profile.Name}.\n"
            + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));
        var tokens = await rig.ReportedUsageAsync(task, ct);
        Assert.True(
            tokens.Sum(u => u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheWriteTokens) > 0,
            $"a usage row landed for {profile.Name} but every token bucket was zero");
        Assert.DoesNotContain(tokens, u => u.CostUsd is not null);
    }

    /// <summary>
    /// §11 park → resume via <c>session/load</c>: a worker asks, the Lead
    /// answers, the park record carries the session ref, and the resumed
    /// instance reports a nonce that existed only in the first turn. Skips
    /// when the profile does not support loadSession.
    /// </summary>
    public static async Task ResumesAfterParkAsync(PostgresFixture pg, RealHarnessProfile profile)
    {
        // Under ACP the gate is the agent's own loadSession capability, not a resume argv.
        // Every agent measured on 2026-08-15 declares it, so this skip should now be dead —
        // tools/acp-probe is how you check before adding a fifth harness.
        Skip.If(!profile.SupportsResume,
            profile.Name + " does not support session/load — park/resume is not this harness's bar");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = cts.Token;

        var remembered = RealHarnessProfiles.NewToken();
        var nonce = "nonce-" + remembered;
        await using var rig = profile.OpenParkRig(pg, nonce);
        await rig.StartAsync(ct);
        using var attach = profile.AttachTo(rig);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(RealHarnessProfile.AskThenStopDescription, ct);

        Assert.True(
            await rig.DispatchUntilAsync(
                task, "A",
                async () => await rig.StateAsync(task, ct)
                    is TaskState.BlockedOnInput or TaskState.Verifying or TaskState.Completed,
                MaxAttempts, PerLegBudget, ct),
            $"the real {profile.Name} worker neither blocked on input nor finished.\n"
            + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));
        var blockedState = await rig.StateAsync(task, ct);
        Assert.True(
            blockedState == TaskState.BlockedOnInput,
            $"the {profile.Name} worker reached {blockedState} without ever asking, so there is "
            + "no park to resume.\n"
            + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var sessionRef = await rig.HarnessSessionRefAsync(task, ct);
        Assert.False(
            string.IsNullOrWhiteSpace(sessionRef),
            $"no harness session ref was stamped for {profile.Name}, so the resume below "
            + "could only cold-start.\n"
            + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));
        Assert.Contains(
            "report the remembered value",
            await rig.QuestionAsync(task, ct),
            StringComparison.OrdinalIgnoreCase);

        await rig.AnswerAsync(task, "Yes — report the remembered value now.", ct);
        var park = await rig.ParkAsync(task, ct);
        Assert.Equal("A", park.Machine);
        Assert.Equal(sessionRef, park.SessionRef);

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(task, "A", MaxAttempts, PerLegBudget, ct),
            $"the resumed {profile.Name} worker never drove its task to verifying.\n"
            + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));

        Assert.Contains(remembered, await rig.ResultReferenceAsync(task, ct));

        var instanceSessions = rig.InstanceSessionIdsOn("A", task, profile.SessionIdFromLine);
        Assert.True(
            instanceSessions.Count >= 2,
            $"resume of {profile.Name} did not produce a second captured instance — the successor "
            + "may have cold-started without a transcript file, or SessionIdFromLine missed the stream.\n"
            + profile.FailureHypotheses + await rig.RealWorkerDiagnosticsAsync(task, ct));
        Assert.All(instanceSessions, id => Assert.Equal(sessionRef, id));
        Assert.Equal("A", rig.MachineRanOn(task));
        Assert.Equal(sessionRef, await rig.HarnessSessionRefAsync(task, ct));
    }
}
