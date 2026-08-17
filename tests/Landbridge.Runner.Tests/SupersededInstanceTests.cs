using Landbridge.Core;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Runner.Tests;

/// <summary>
/// §11 park-resume overlap (#102): a task can be dispatched to this machine while the
/// previous instance of it is still winding down, because the plane finishes with an
/// instance without waiting for its process — the transition that redispatches a parked
/// task revokes that instance's token (§5), and a headless worker that asked a question
/// exits on its own schedule, milliseconds later.
///
/// <para>A worker instance is identified here by task id alone, which is what made that
/// overlap destructive: the predecessor's ordinary death acted on its successor. These
/// facts pin each of the three ways it did, because each one on its own is enough to
/// break a resume, and the plane cannot defend against any of them — the wire names only
/// the task (§10, frozen), so an exit reported for one instance is indistinguishable from
/// the other's.</para>
/// </summary>
public sealed class SupersededInstanceTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);

    /// <summary>
    /// Everything the ring carries up to and including the task's exit event, waiting for it
    /// rather than draining whatever happens to have landed — the exit is enqueued from the
    /// process's own <c>Exited</c> handler, so a snapshot taken right after a kill races it.
    /// Returns what arrived if the exit never comes, so a missing one fails on the assertion
    /// rather than on a timeout.
    /// </summary>
    private async Task<List<RunnerEvent>> EventsThroughExitAsync(SessionId task, TimeSpan timeout)
    {
        var events = new List<RunnerEvent>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var item in _ring.ReadAllAsync(cts.Token))
            {
                events.Add(item.Event);
                if (item.Event is ExitedEvent x && x.Session == task)
                    break;
            }
        }
        catch (OperationCanceledException) { /* report what arrived */ }
        return events;
    }

    /// <summary>
    /// The predecessor's death is not reported as the task's. Were it reported, the plane
    /// would read it as the death of the instance that is current by then — requeuing the
    /// task and revoking the resumed worker's token while it runs, which is the "MCP server
    /// needs re-authorization" the resumed agent reported in #102.
    /// </summary>
    [Fact]
    public async Task A_superseded_predecessors_exit_is_not_reported_as_the_tasks_exit()
    {
        var task = SessionId.New();
        var supervisor = new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("sleeper"), "m");
        Assert.True(supervisor.TryGet(task, out var predecessor));

        // The redispatch. Same task, same machine, while the predecessor still runs.
        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("sleeper"), "m");
        Assert.True(supervisor.TryGet(task, out var successor));
        Assert.NotSame(predecessor, successor);

        Assert.True(
            await TestKit.WaitUntilAsync(() => !predecessor.ProcessAlive, TimeSpan.FromSeconds(15)),
            "the superseded predecessor was left running");
        Assert.True(
            await TestKit.WaitUntilAsync(() => supervisor.SupersededExits == 1, TimeSpan.FromSeconds(15)),
            "the predecessor's exit was never observed as superseded");

        // The successor is untouched and still the task's one worker.
        Assert.True(successor.ProcessAlive);
        Assert.Equal(1, supervisor.RunningTotal);

        Assert.True(supervisor.Kill(task));
        var events = await EventsThroughExitAsync(task, TimeSpan.FromSeconds(15));

        // Two starts, and exactly one exit — the successor's, on the kill above. The
        // predecessor's died first, so had it been reported it would already be in here.
        Assert.Equal(2, events.Count(e => e is StartedEvent s && s.Session == task));
        Assert.Single(events, e => e is ExitedEvent x && x.Session == task);
    }

    /// <summary>
    /// The successor stays supervised. The predecessor's exit used to remove the task's
    /// entry — by then the successor's — leaving a live worker with no liveness bumps, no
    /// stop delivery, no kill, and a stray at teardown.
    /// </summary>
    [Fact]
    public async Task A_superseded_predecessors_exit_does_not_drop_its_successor_from_supervision()
    {
        var task = SessionId.New();
        var supervisor = new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("sleeper"), "m");
        Assert.True(supervisor.TryGet(task, out var predecessor));
        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("sleeper"), "m");

        Assert.True(await TestKit.WaitUntilAsync(() => !predecessor.ProcessAlive, TimeSpan.FromSeconds(15)));

        Assert.True(supervisor.TryGet(task, out var stillSupervised));
        Assert.True(stillSupervised.ProcessAlive);
        Assert.Contains(task, supervisor.RunningSessions);
        Assert.Equal(1, supervisor.RunningFor("default"));

        // And it is killable through the task id, which is the property a requeue depends on.
        Assert.True(supervisor.Kill(task));
    }

    /// <summary>
    /// The predecessor's exit does not reap its successor. Task-exit stray cleanup matches
    /// on <c>LANDBRIDGE_SESSION_ID</c>, which every instance of a task carries — so run for a
    /// superseded instance it kills the process tree of the very worker that replaced it.
    /// This is the half that killed the resumed claude outright in #102, before it could
    /// call a single landbridge tool.
    /// </summary>
    [Fact]
    public async Task A_superseded_predecessors_exit_does_not_reap_its_successor_by_task_id()
    {
        var task = SessionId.New();
        var inventory = new MutableProcessInventory();
        var supervisor = new ProcessSupervisor(
            TestKit.Machine(_workRoot), _ring, _clock,
            new StrayReaper(inventory, selfPid: Environment.ProcessId));

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("sleeper"), "m");
        Assert.True(supervisor.TryGet(task, out var predecessor));

        // The redispatch, while the predecessor still runs.
        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("sleeper"), "m");
        Assert.True(supervisor.TryGet(task, out var successor));
        var successorPid = successor.Process.Id;

        // What the reaper would discover if it ran: the successor, on this machine, carrying
        // this task id. It is indistinguishable from a stray on that key — which is the point.
        inventory.Processes = [new TaggedProcess(successorPid, "m", task.ToString())];

        Assert.True(await TestKit.WaitUntilAsync(() => !predecessor.ProcessAlive, TimeSpan.FromSeconds(15)));
        Assert.True(
            await TestKit.WaitUntilAsync(() => supervisor.SupersededExits == 1, TimeSpan.FromSeconds(15)),
            "the predecessor's exit was never observed as superseded");

        // The successor survived its predecessor's death.
        Assert.True(TestKit.PidAlive(successorPid), "the predecessor's exit reaped its successor");
        Assert.True(successor.ProcessAlive);

        Assert.True(supervisor.Kill(task));
    }

    /// <summary>An ordinary exit still reports, so the suppression above is scoped to a
    /// superseded instance and has not quietly disabled the requeue path (§10, #84).</summary>
    [Fact]
    public async Task An_unsuperseded_workers_exit_is_still_reported()
    {
        var task = SessionId.New();
        var supervisor = new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("sleeper"), "m");
        Assert.True(supervisor.TryGet(task, out var only));
        Assert.True(supervisor.Kill(task));
        Assert.True(await TestKit.WaitUntilAsync(() => !only.ProcessAlive, TimeSpan.FromSeconds(15)));

        var events = await EventsThroughExitAsync(task, TimeSpan.FromSeconds(15));
        Assert.Contains(events, e => e is ExitedEvent x && x.Session == task);
        Assert.Equal(0, supervisor.SupersededExits);
    }

    public void Dispose() => TestKit.TryDeleteRoot(_workRoot);

    /// <summary>An inventory seeded after the pids it must report exist — the shared
    /// <see cref="FakeProcessInventory"/> fixes its list at construction, and a live
    /// successor's pid is only known once it has been spawned.</summary>
    private sealed class MutableProcessInventory : IProcessInventory
    {
        public IReadOnlyList<TaggedProcess> Processes { get; set; } = [];

        public IReadOnlyList<TaggedProcess> ListLandbridgeProcesses() => Processes;
    }
}
