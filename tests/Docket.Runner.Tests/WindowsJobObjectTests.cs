using System.Diagnostics;
using System.Runtime.Versioning;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// The Windows half of stray cleanup (§10): a per-worker kill-on-close Job
/// Object (<see cref="WindowsJobObject"/>). Gated to Windows — these run for real on
/// the OS-matrix CI <c>windows-latest</c> leg and skip cleanly on this macOS box,
/// exactly like <see cref="MacOsProcessInventoryTests"/> is gated to darwin. They use
/// the same real-spawned-child + atomic-marker + bounded-wait rig as the rest of the
/// runner suite (no shell, argv only).
///
/// <para>The core guarantee they prove: assigning a worker to a kill-on-close job and
/// then closing the sole job handle terminates the worker AND its grandchild —
/// which is precisely what the OS does to docketd's handle when docketd dies, so it
/// simulates docketd death without killing the test host (the same trick
/// <see cref="DeadMansSwitchTests"/> uses by closing a stdin pipe).</para>
/// </summary>
public sealed class WindowsJobObjectTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly List<Process> _spawned = [];

    /// <summary>
    /// Starts the harness with its stdin redirected and HELD (never closed) so no EOF
    /// trips the dead-man switch — isolating the job-close as the only thing that can
    /// end the tree. No Windows API here, so it is not platform-gated.
    /// </summary>
    private Process StartHeldHarness(string mode, string workDir)
    {
        var psi = new ProcessStartInfo(TestKit.HarnessPath())
        {
            UseShellExecute = false,
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
        };
        psi.ArgumentList.Add(mode);
        var process = Process.Start(psi)!;
        _spawned.Add(process);
        return process;
    }

    /// <summary>
    /// (1) A worker spawned through the supervisor is assigned to its own job. Queried
    /// via <c>IsProcessInJob</c> against THAT job, so a nested outer job (CI runners
    /// wrap processes in their own jobs) doesn't give a false positive.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Supervisor_seals_each_worker_into_a_kill_on_close_job()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: kernel32 Job Objects.");

        var supervisor = new ProcessSupervisor(
            TestKit.Machine(_workRoot), new OutboundEventRing(capacity: 256), new FakeTimeProvider());
        var task = SessionId.New();

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("run"), "m");

        var started = Path.Combine(_workRoot, task.ToString(), "started");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(started), TimeSpan.FromSeconds(15)),
            "worker never started under the supervisor");

        Assert.True(supervisor.TryGet(task, out var st));
        // If the environment refused the assignment, the supervisor degrades (the
        // dead-man pipe still covers it); report rather than hard-fail that quirk.
        Skip.If(st.Job is null,
            $"Job assignment degraded in this environment: {supervisor.LastJobAssignmentFailure}");
        Assert.True(st.Job!.ContainsProcess(st.Process.Handle), "worker was not contained in its job");

        supervisor.Kill(task);
        Assert.True(await TestKit.WaitUntilAsync(() => !st.ProcessAlive, TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// (2) Containment proof: create a kill-on-close job, start a spawn-child tree,
    /// assign it, then CLOSE the sole handle — the parent and the grandchild must die
    /// promptly. Closing the handle is byte-for-byte what the OS does to docketd's
    /// handle on death, so this proves the primitive without killing docketd itself.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Closing_a_kill_on_close_job_takes_the_whole_tree_down()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: kernel32 Job Objects.");

        var harness = StartHeldHarness("spawn-child", _workRoot);

        var job = WindowsJobObject.TryCreateAndAssign(harness.Handle, out var failure);
        Skip.If(job is null, $"AssignProcessToJobObject degraded in this environment: {failure}");

        var childPidPath = Path.Combine(_workRoot, "child.pid");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(childPidPath), TimeSpan.FromSeconds(15)),
            "harness never recorded its grandchild pid");
        var grandchildPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
        Assert.True(await TestKit.WaitUntilAsync(() => TestKit.PidAlive(grandchildPid), TimeSpan.FromSeconds(10)),
            "grandchild never came up");

        // Containment scope: the grandchild forked AFTER assignment is placed in our
        // job automatically by the kernel. Prove it before we tear the job down.
        using (var grandchild = Process.GetProcessById(grandchildPid))
            Assert.True(job!.ContainsProcess(grandchild.Handle), "grandchild was not contained in the job");

        // Close the only handle → kill-on-close → the whole tree dies. We still hold
        // the harness's stdin write end (never closed), so this is the job, not EOF.
        job!.Close();

        Assert.True(await TestKit.WaitUntilAsync(() => harness.HasExited, TimeSpan.FromSeconds(15)),
            "harness survived the kill-on-close job close");
        Assert.True(await TestKit.WaitUntilAsync(() => !TestKit.PidAlive(grandchildPid), TimeSpan.FromSeconds(15)),
            "grandchild survived the kill-on-close job close");
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
