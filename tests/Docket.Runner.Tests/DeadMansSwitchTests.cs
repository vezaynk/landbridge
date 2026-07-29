using System.Diagnostics;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// The harness dead-man's switch (§10): a spawned worker self-terminates when
/// docketd is gone, before any restart sweep runs. docketd redirects a worker's
/// stdin and holds the write end for the worker's lifetime; if docketd dies the OS
/// closes the pipe and the harness sees EOF. These exercise that against real
/// spawned processes — no shell, argv only — with the atomic markers the harness
/// writes and generous bounded waits, matching the rest of the runner suite.
/// </summary>
public sealed class DeadMansSwitchTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly List<Process> _spawned = [];

    private Process StartHarness(string mode, string workDir, bool holdStdin)
    {
        var psi = new ProcessStartInfo(TestKit.HarnessPath())
        {
            UseShellExecute = false,
            WorkingDirectory = workDir,
            RedirectStandardInput = holdStdin,
        };
        psi.ArgumentList.Add(mode);
        var process = Process.Start(psi)!;
        _spawned.Add(process);
        return process;
    }

    /// <summary>
    /// Portable, required (must pass on this macOS box). Closing the stdin write end
    /// is byte-identical to what the OS does when the pipe holder (docketd) dies —
    /// so this faithfully simulates docketd death without killing anything. The
    /// harness must trip the switch: write the deadman marker, exit with the
    /// distinct code, and take its grandchild down with it.
    /// </summary>
    [Fact]
    public async Task Stdin_eof_trips_the_switch_writes_the_marker_and_reaps_the_grandchild()
    {
        var harness = StartHarness("spawn-child", _workRoot, holdStdin: true);

        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(Path.Combine(_workRoot, "started")), TimeSpan.FromSeconds(15)),
            "harness never wrote its started marker");
        var childPidPath = Path.Combine(_workRoot, "child.pid");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(childPidPath), TimeSpan.FromSeconds(15)),
            "harness never recorded its grandchild pid");
        var grandchildPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
        Assert.True(await TestKit.WaitUntilAsync(() => TestKit.PidAlive(grandchildPid), TimeSpan.FromSeconds(10)));

        // Close the write end = pipe EOF = the kernel signal a dying docketd sends.
        harness.StandardInput.Close();

        var deadman = Path.Combine(_workRoot, "deadman");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(deadman), TimeSpan.FromSeconds(15)),
            "harness did not write its deadman marker on stdin EOF");
        Assert.True(await TestKit.WaitUntilAsync(() => harness.HasExited, TimeSpan.FromSeconds(15)),
            "harness did not exit on stdin EOF");
        Assert.Equal(TestHarness.Program.DeadManExitCode, harness.ExitCode);
        Assert.True(await TestKit.WaitUntilAsync(() => !TestKit.PidAlive(grandchildPid), TimeSpan.FromSeconds(15)),
            "grandchild survived the dead-man switch");
    }

    /// <summary>
    /// True parent death (best-effort; runs here on macOS). A `middleman` harness
    /// stands in for docketd: it spawns an inner spawn-child harness, holds the
    /// inner's stdin exactly as docketd would, and sleeps. SIGKILLing ONLY the
    /// middleman (not the tree) is a real parent death — the OS closes the inner's
    /// stdin pipe, and the inner must trip its own switch and take its grandchild.
    /// </summary>
    [Fact]
    public async Task Killing_the_true_parent_trips_the_inner_switch()
    {
        // We hold the middleman's stdin like docketd would, and never close it — the
        // SIGKILL below, not a pipe close, is what ends it.
        var middleman = StartHarness("middleman", _workRoot, holdStdin: true);

        var innerPidPath = Path.Combine(_workRoot, "inner.pid");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(innerPidPath), TimeSpan.FromSeconds(15)),
            "middleman never recorded the inner harness pid");
        var innerPid = int.Parse(await File.ReadAllTextAsync(innerPidPath));

        var childPidPath = Path.Combine(_workRoot, "child.pid");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(childPidPath), TimeSpan.FromSeconds(15)),
            "inner harness never recorded its grandchild pid");
        var grandchildPid = int.Parse(await File.ReadAllTextAsync(childPidPath));

        Assert.True(await TestKit.WaitUntilAsync(
            () => TestKit.PidAlive(innerPid) && TestKit.PidAlive(grandchildPid), TimeSpan.FromSeconds(15)),
            "inner harness and its grandchild never came up");

        // SIGKILL the middleman ONLY (no tree) — the inner and grandchild must die
        // from the switch, not from the kill.
        middleman.Kill(entireProcessTree: false);

        Assert.True(await TestKit.WaitUntilAsync(() => !TestKit.PidAlive(innerPid), TimeSpan.FromSeconds(20)),
            "inner harness survived its true parent's death");
        Assert.True(await TestKit.WaitUntilAsync(() => !TestKit.PidAlive(grandchildPid), TimeSpan.FromSeconds(20)),
            "inner's grandchild survived the dead-man switch");
    }

    /// <summary>
    /// Supervisor integration: docketd redirects and HOLDS the worker's stdin, so a
    /// live supervisor must NOT let the harness trip its switch. (Message-mode stop
    /// still working over that same held pipe is proven by
    /// <see cref="ProcessSupervisorTests.Stop_message_mode_reaches_the_agent_as_a_turn_and_it_winds_down"/>.)
    /// </summary>
    [Fact]
    public async Task Supervisor_holds_stdin_so_the_harness_does_not_trip_while_docketd_lives()
    {
        var ring = new OutboundEventRing(capacity: 256);
        var supervisor = new ProcessSupervisor(TestKit.Machine(_workRoot), ring, new FakeTimeProvider());
        var task = TaskId.New();

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("spawn-child"), "m");
        var workDir = Path.Combine(_workRoot, task.ToString());
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(Path.Combine(workDir, "started")), TimeSpan.FromSeconds(15)),
            "harness never started under the supervisor");

        // Held stdin means no EOF: the switch must stay unarmed while docketd lives.
        var deadman = Path.Combine(workDir, "deadman");
        var tripped = await TestKit.WaitUntilAsync(() => File.Exists(deadman), TimeSpan.FromSeconds(2));
        Assert.False(tripped, "harness tripped its dead-man switch while the supervisor still held stdin");
        Assert.Equal(1, supervisor.RunningTotal);

        supervisor.Kill(task);
    }

    /// <summary>
    /// Linux-only PDEATHSIG (skips here on macOS; runs in CI on ubuntu). The
    /// `middleman-nopipe` rig does NOT hold a pipe on the inner's stdin, so there is
    /// no EOF to trip the portable watch — only <c>prctl(PR_SET_PDEATHSIG, SIGKILL)</c>
    /// can end the inner when its parent is SIGKILLed. Asserts the guarantee: a
    /// direct child dies promptly after a hard parent death.
    /// </summary>
    [SkippableFact]
    public async Task Pdeathsig_kills_the_direct_child_when_the_parent_dies_hard()
    {
        Skip.IfNot(OperatingSystem.IsLinux(),
            "Linux-only: prctl(PR_SET_PDEATHSIG). The stdin-EOF tests cover the portable guarantee on macOS.");

        var middleman = StartHarness("middleman-nopipe", _workRoot, holdStdin: true);

        var innerPidPath = Path.Combine(_workRoot, "inner.pid");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(innerPidPath), TimeSpan.FromSeconds(15)),
            "middleman-nopipe never recorded the inner pid");
        var innerPid = int.Parse(await File.ReadAllTextAsync(innerPidPath));

        // `ready` means the inner armed PDEATHSIG and is sleeping — safe to kill now.
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(Path.Combine(_workRoot, "ready")), TimeSpan.FromSeconds(15)),
            "inner sleeper never armed PDEATHSIG");
        Assert.True(TestKit.PidAlive(innerPid), "inner sleeper was not alive before the kill");

        middleman.Kill(entireProcessTree: false);

        Assert.True(await TestKit.WaitUntilAsync(() => !TestKit.PidAlive(innerPid), TimeSpan.FromSeconds(20)),
            "inner sleeper survived its parent's hard death — PDEATHSIG did not fire");
    }

    public void Dispose()
    {
        foreach (var p in _spawned)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            p.Dispose();
        }
        TestKit.TryDeleteRoot(_workRoot);
    }
}
