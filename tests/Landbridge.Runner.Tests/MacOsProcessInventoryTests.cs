using System.Diagnostics;

namespace Docket.Runner.Tests;

/// <summary>
/// macOS discovery half of stray cleanup (§10 runner restart). Unlike
/// <see cref="StrayReaperTests"/>, which seeds a fake inventory, these exercise
/// the real <see cref="MacOsProcessInventory"/> against real spawned processes:
/// on darwin it must actually read another same-user process's environment
/// (libproc + <c>sysctl(KERN_PROCARGS2)</c>, unprivileged) and find the docket
/// tags. Gated to macOS — the discovery path is inherently platform-specific.
/// </summary>
public class MacOsProcessInventoryTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly List<Process> _spawned = [];

    private Process SpawnChild(string? machineId, string? sessionId = null)
    {
        var psi = new ProcessStartInfo(TestKit.HarnessPath()) { UseShellExecute = false };
        psi.ArgumentList.Add("child"); // bare sleeper — no markers, just lives until killed
        if (machineId is not null)
            psi.Environment["DOCKET_MACHINE_ID"] = machineId;
        if (sessionId is not null)
            psi.Environment["DOCKET_SESSION_ID"] = sessionId;
        var process = Process.Start(psi)!;
        _spawned.Add(process);
        return process;
    }

    [SkippableFact]
    public async Task Discovers_a_tagged_process_by_env_then_reaps_it()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "macOS-only: reads process env via libproc + KERN_PROCARGS2.");

        // Fresh guids so nothing but our own child can match this run.
        var machineId = Guid.NewGuid().ToString();
        var sessionId = Guid.NewGuid().ToString();

        var tagged = SpawnChild(machineId, sessionId);
        var untagged = SpawnChild(machineId: null); // no DOCKET_MACHINE_ID — must never be listed

        var inventory = new MacOsProcessInventory();

        // Poll: the argv/env region is readable once the child has exec'd; give it
        // a moment rather than assuming it's visible the instant Start() returns.
        TaggedProcess match = default;
        var appeared = await TestKit.WaitUntilAsync(
            () =>
            {
                foreach (var p in inventory.ListDocketProcesses())
                {
                    if (p.Pid == tagged.Id)
                    {
                        match = p;
                        return true;
                    }
                }
                return false;
            },
            TimeSpan.FromSeconds(10));

        Assert.True(appeared, $"MacOsProcessInventory never discovered tagged pid {tagged.Id}");
        output.WriteLine($"discovered pid={match.Pid} machineId={match.MachineId} sessionId={match.SessionId}");
        Assert.Equal(machineId, match.MachineId);
        Assert.Equal(sessionId, match.SessionId);

        // The untagged child (no DOCKET_MACHINE_ID) must not be discovered at all.
        Assert.DoesNotContain(inventory.ListDocketProcesses(), p => p.Pid == untagged.Id);

        // Reap half, live discovery: finds and kills the tagged process for real.
        var reaper = new StrayReaper(inventory, selfPid: Environment.ProcessId);
        var reaped = reaper.Reap(machineId);

        Assert.True(reaped >= 1, $"expected Reap to kill at least the tagged process, got {reaped}");
        Assert.True(
            await TestKit.WaitUntilAsync(() => !TestKit.PidAlive(tagged.Id), TimeSpan.FromSeconds(5)),
            "tagged process should be dead after Reap");
        Assert.True(TestKit.PidAlive(untagged.Id), "untagged process must survive a machine-scoped reap");
    }

    public void Dispose()
    {
        foreach (var p in _spawned)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            p.Dispose();
        }
    }
}
