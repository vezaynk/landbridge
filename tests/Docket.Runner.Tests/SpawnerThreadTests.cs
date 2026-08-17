using System.Collections.Concurrent;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// The dedicated spawner thread (PDEATHSIG thread affinity). docketd must fork every worker from ONE
/// long-lived, non-thread-pool OS thread: on Linux the harness arms PDEATHSIG, which
/// the kernel keys to the forking <em>thread</em>, so forking from a thread-pool
/// thread that the pool later retires would spuriously SIGKILL a healthy worker.
/// These are portable and run on every OS — the unit tests exercise
/// <see cref="SpawnerThread"/> directly; the integration tests prove
/// <see cref="ProcessSupervisor"/> routes real spawns through it.
/// </summary>
public sealed class SpawnerThreadTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

    [Fact]
    public void Run_executes_all_work_on_a_single_dedicated_non_threadpool_thread()
    {
        var spawner = new SpawnerThread();
        var observed = new ConcurrentBag<(int Id, bool IsPool)>();

        // Call Run from thread-pool callers; the work must still land on ONE non-pool
        // thread — the PDEATHSIG-safe invariant. Parallel.For bodies run on the pool.
        Parallel.For(0, 8, _ =>
            spawner.Run(() =>
                observed.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread))));

        Assert.Equal(8, observed.Count);
        Assert.All(observed, o => Assert.False(o.IsPool, "spawn ran on a thread-pool thread"));
        Assert.Single(observed.Select(o => o.Id).Distinct()); // exactly one spawner thread
        Assert.Equal(spawner.ManagedThreadId, observed.First().Id);

        spawner.Close();
    }

    [Fact]
    public void Run_propagates_the_works_exception_and_keeps_serving()
    {
        var spawner = new SpawnerThread();

        var ex = Assert.Throws<InvalidOperationException>(
            () => spawner.Run(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", ex.Message);

        // A faulted work item must not wedge the thread; the next spawn still runs.
        var ran = false;
        spawner.Run(() => ran = true);
        Assert.True(ran);

        spawner.Close();
    }

    [Fact]
    public async Task Supervisor_spawns_on_the_dedicated_non_threadpool_thread()
    {
        var task = SessionId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("run"), "m");

        var obs = supervisor.LastSpawnThreadObservation;
        Assert.NotNull(obs);
        Assert.False(obs!.Value.IsThreadPoolThread); // PDEATHSIG thread affinity: not a retiring pool thread
        Assert.Equal(supervisor.SpawnerManagedThreadId, obs.Value.ManagedThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, obs.Value.ManagedThreadId); // not inline

        // Sanity: the spawn actually produced a live worker.
        var started = Path.Combine(_workRoot, task.ToString(), "started");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(started), TimeSpan.FromSeconds(15)));

        supervisor.Kill(task);
    }

    [Fact]
    public async Task Concurrent_spawns_serialize_through_the_one_thread_and_both_start()
    {
        var supervisor = Supervisor();
        var a = SessionId.New();
        var b = SessionId.New();

        // Two concurrent Spawn calls, each blocking on the single spawner thread; both
        // must succeed (serialized, not dropped or deadlocked).
        await Task.WhenAll(
            Task.Run(() => supervisor.Spawn(TestKit.Dispatch(a), TestKit.Profile("run"), "m")),
            Task.Run(() => supervisor.Spawn(TestKit.Dispatch(b), TestKit.Profile("run"), "m")));

        Assert.Equal(2, supervisor.RunningTotal);

        var startedA = Path.Combine(_workRoot, a.ToString(), "started");
        var startedB = Path.Combine(_workRoot, b.ToString(), "started");
        Assert.True(await TestKit.WaitUntilAsync(
            () => File.Exists(startedA) && File.Exists(startedB), TimeSpan.FromSeconds(15)),
            "both concurrently-spawned workers should start");
        Assert.False(supervisor.LastSpawnThreadObservation!.Value.IsThreadPoolThread);

        supervisor.KillAll();
    }

    public void Dispose()
    {
        try { _supervisor?.KillAll(); } catch { /* best effort */ }
        TestKit.TryDeleteRoot(_workRoot);
    }
}
