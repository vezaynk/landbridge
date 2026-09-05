using System.Collections.Concurrent;
using Landbridge.Contracts;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane;

/// <summary>
/// Relays a worker's §10 process commands — start, stop, write — to the machine holding its
/// task, and waits for that machine's answer. Same request/reply shape as the §12 transcript
/// read: register a waiter by request id, send, park, and let the
/// <see cref="RunnerEventSink"/> complete it.
///
/// <para><b>The plane decides nothing here.</b> Whether a task may start a process is its
/// profile's policy, and a profile's meaning is machine-local data the control plane never
/// learns (§10). This resolves which machine holds the task, forwards the request verbatim,
/// and reports back whatever the machine said — every refusal in a result came from the
/// runner.</para>
///
/// <para><b>Machine-scoped, not task-scoped.</b> A process outlives the task that started it,
/// so stop and write are addressed by name on a machine rather than by ownership. That is what
/// lets a Lead's cleanup continuation — a new task id, dispatched to the same machine — stop
/// what an earlier task started. Cleanup is orchestration, not enforcement.</para>
/// </summary>
public sealed class ProcessControlRelay(
    RunnerConnectionRegistry registry,
    IDbContextFactory<LandbridgeDbContext>? dbFactory = null)

{
    /// <summary>How long a machine has to answer a start. Generous: the runner spawns the
    /// OS process and replies once it is up. A process declares no port.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Stop and write are local acts on the machine; they should be quick or broken.</summary>
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<RunnerEvent>> _waiters =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Hand a machine's reply to whoever is waiting. Never blocks: the sink runs on the runner
    /// socket's receive loop, so anything awaited here delays every other inbound frame —
    /// heartbeats and <c>alive</c> events included. A reply nobody waits for is dropped.
    /// </summary>
    public bool Complete(RunnerEvent reply)
    {
        var requestId = reply switch
        {
            ProcessStartedEvent e => e.RequestId,
            ProcessStoppedEvent e => e.RequestId,
            ProcessWrittenEvent e => e.RequestId,
            _ => null,
        };
        return requestId is not null
            && _waiters.TryGetValue(requestId, out var tcs)
            && tcs.TrySetResult(reply);
    }

    /// <summary>Start an agent-declared background process on the machine holding this task.</summary>
    public async Task<ProcessStartOutcome> StartAsync(
        SessionId task, string name, IReadOnlyList<string> spawn, string? workingDirectory,
        IReadOnlyDictionary<string, string>? env, bool openStdin, CancellationToken ct)
    {
        var sent = await AskAsync(
            task, StartTimeout,
            requestId => new StartProcessCommand(
                task, requestId, name, spawn, workingDirectory, env, openStdin),
            ct);

        return sent switch
        {
            Answer.Unreachable u => new ProcessStartOutcome(false, null, u.Why),
            Answer.Reply { Event: ProcessStartedEvent e } =>
                new ProcessStartOutcome(e.Started, e.LogPath, e.Refusal),
            _ => new ProcessStartOutcome(false, null, "the machine sent an unexpected reply"),
        };
    }

    /// <summary>Stop a named process on the machine holding this task.</summary>
    public async Task<ProcessControlOutcome> StopAsync(SessionId task, string name, CancellationToken ct)
    {
        var sent = await AskAsync(
            task, ControlTimeout, requestId => new StopProcessCommand(task, requestId, name), ct);

        return sent switch
        {
            Answer.Unreachable u => new ProcessControlOutcome(false, u.Why, null),
            Answer.Reply { Event: ProcessStoppedEvent e } =>
                new ProcessControlOutcome(e.Stopped, e.Refusal, e.ExitCode),
            _ => new ProcessControlOutcome(false, "the machine sent an unexpected reply", null),
        };
    }

    /// <summary>Write to a named process's stdin on the machine holding this task.</summary>
    public async Task<ProcessControlOutcome> WriteAsync(
        SessionId task, string name, string data, bool appendNewline, CancellationToken ct)
    {
        var sent = await AskAsync(
            task, ControlTimeout,
            requestId => new WriteProcessCommand(task, requestId, name, data, appendNewline), ct);

        return sent switch
        {
            Answer.Unreachable u => new ProcessControlOutcome(false, u.Why, null),
            Answer.Reply { Event: ProcessWrittenEvent e } =>
                new ProcessControlOutcome(e.Written, e.Refusal, e.Bytes),
            _ => new ProcessControlOutcome(false, "the machine sent an unexpected reply", null),
        };
    }

    /// <summary>
    /// §10: the agent-started processes the machine holding this task last reported.
    /// Read from <c>machine_processes</c>.

    /// </summary>
    public async Task<IReadOnlyList<RunningThing>> ListAsync(SessionId task, CancellationToken ct = default)
    {
        var machine = registry.MachineFor(task);
        if (machine is null)
            return [];

        if (!Guid.TryParse(machine, out var machineId) || dbFactory is null)
            return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.MachineProcesses.AsNoTracking()
            .Where(p => p.MachineId == machineId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
        return rows.Select(AsRunning).ToList();

    }

    private static RunningThing AsRunning(MachineProcessRow p) =>
        new(p.Name, p.State.ToLowerInvariant(), p.StartedAt, p.ExitCode, p.ExitedAt, p.StdinOpen);


    private async Task<Answer> AskAsync(
        SessionId task, TimeSpan timeout, Func<string, RunnerCommand> build, CancellationToken ct)
    {
        var machine = registry.MachineFor(task);
        if (machine is null)
        {
            // No live lease means no machine to act on. A process lives on a machine, so a task
            // that is nowhere has nowhere to address.
            return new Answer.Unreachable(
                "this task is not currently held by any machine, so there is nothing to act on");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RunnerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[requestId] = tcs;
        try
        {
            if (!await registry.SendAsync(machine, build(requestId), ct))
                return new Answer.Unreachable("the machine holding this task is unreachable");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                return new Answer.Reply(await tcs.Task.WaitAsync(deadline.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new Answer.Unreachable(
                    $"the machine did not answer within {timeout.TotalSeconds:0}s");
            }
        }
        finally
        {
            _waiters.TryRemove(requestId, out _);
        }
    }

    private abstract record Answer
    {
        private Answer() { }

        public sealed record Reply(RunnerEvent Event) : Answer;

        public sealed record Unreachable(string Why) : Answer;
    }
}

/// <summary>
/// What a worker learns from <c>start_process</c>. No port: this is a process manager, and
/// reachability is §8.2's noun. <see cref="LogPath"/> is where the machine captured this run, so
/// the agent reads its own process's output with ordinary file tools.
/// </summary>
public sealed record ProcessStartOutcome(bool Started, string? LogPath, string? Refusal);

/// <summary>One agent-started process on a machine (§10).</summary>
/// <param name="StdinOpen">Whether a graceful (EOF) stop is available. False means stopping it is
/// a bounded wait and then a tree kill.</param>
public sealed record RunningThing(
    string Name, string State,
    DateTimeOffset? StartedAt, int? ExitCode, DateTimeOffset? EndedAt, bool StdinOpen);

/// <summary>What a worker learns from <c>stop_process</c> or <c>write_process</c>.
/// <see cref="Value"/> is the exit code for a stop, or the byte count for a write.</summary>
public sealed record ProcessControlOutcome(bool Ok, string? Refusal, int? Value);
