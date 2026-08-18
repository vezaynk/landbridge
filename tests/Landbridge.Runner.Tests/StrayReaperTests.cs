using System.Diagnostics;

namespace Landbridge.Runner.Tests;

/// <summary>
/// Stray-process cleanup, spec §10 runner restart. Discovery is abstracted
/// behind <see cref="IProcessInventory"/> (platform-specific — see the source),
/// so these tests seed a fake inventory with the pids they spawned and exercise
/// the portable <b>kill</b> half against real processes.
/// </summary>
public class StrayReaperTests : IDisposable
{
    private readonly List<Process> _spawned = [];

    private Process SpawnStray(string machineId, string? sessionId = null)
    {
        var psi = new ProcessStartInfo(TestKit.HarnessPath()) { UseShellExecute = false };
        psi.ArgumentList.Add("child");
        psi.Environment["LANDBRIDGE_MACHINE_ID"] = machineId;
        if (sessionId is not null)
            psi.Environment["LANDBRIDGE_SESSION_ID"] = sessionId;
        var process = Process.Start(psi)!;
        _spawned.Add(process);
        return process;
    }

    [Fact]
    public async Task Reap_kills_only_processes_tagged_with_this_machine()
    {
        var a1 = SpawnStray("machine-A");
        var a2 = SpawnStray("machine-A");
        var b1 = SpawnStray("machine-B");

        var inventory = new FakeProcessInventory(
            new TaggedProcess(a1.Id, "machine-A", null),
            new TaggedProcess(a2.Id, "machine-A", null),
            new TaggedProcess(b1.Id, "machine-B", null));
        var reaper = new StrayReaper(inventory, selfPid: Environment.ProcessId);

        var reaped = reaper.Reap("machine-A");

        Assert.Equal(2, reaped);
        Assert.True(await TestKit.WaitUntilAsync(() => !TestKit.PidAlive(a1.Id), TimeSpan.FromSeconds(5)));
        Assert.True(await TestKit.WaitUntilAsync(() => !TestKit.PidAlive(a2.Id), TimeSpan.FromSeconds(5)));
        Assert.True(TestKit.PidAlive(b1.Id)); // a different machine's process is untouched
    }

    [Fact]
    public void Reap_never_kills_the_daemon_itself()
    {
        // A daemon whose own pid somehow appears in the inventory must not
        // suicide (§10 — reap strays, not self).
        var inventory = new FakeProcessInventory(
            new TaggedProcess(Environment.ProcessId, "machine-A", null));
        var reaper = new StrayReaper(inventory, selfPid: Environment.ProcessId);

        Assert.Equal(0, reaper.Reap("machine-A"));
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
