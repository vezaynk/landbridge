using System.Collections.Concurrent;
using Landbridge.Contracts;
using Landbridge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Landbridge.ControlPlane;

/// <summary>
/// Relays one transcript read from a machine to a human operator (spec §12 serving). The
/// plane holds no transcript bytes: it asks the machine that ran the dispatch for one
/// range, hands the reply straight to the caller, and keeps nothing — no cache, no row, no
/// log of the content.
///
/// <para><b>Pull, one range in flight.</b> Each call is one command and one reply
/// (<see cref="TranscriptWaiters"/>), so the caller's own pace is the flow control: it asks
/// for the next range only once it has written the last to the operator's connection. That
/// is what keeps a 50 MiB transcript from queueing behind itself on the control socket and
/// starving heartbeats or a <c>kill</c> (§10 channel separation).</para>
///
/// <para><b>The terminal gate lives here, not in the endpoint.</b> Redaction is
/// unresolved (§13, §16 open question 8), so transcripts are served verbatim and the
/// compensating control is scope: only a task that can never run again, and whose worker
/// instance token is therefore already revoked, is readable. Keeping the check inside the
/// service means no future caller can forget it — the endpoint decides <em>who</em> is
/// asking (a human operator, §12), this decides <em>what</em> may be asked for.</para>
///
/// <para>A singleton alongside <see cref="RunnerConnectionRegistry"/> and
/// <see cref="TranscriptWaiters"/>, resolving a scoped <see cref="SessionStore"/> per call for
/// the state read — the <see cref="RunnerEventSink"/> pattern. Which machines ran a task is
/// a query the caller supplies (<see cref="DashboardQueries"/>); this class only talks to a
/// machine.</para>
/// </summary>
public sealed class TranscriptRelayService(
    IServiceScopeFactory scopes,
    RunnerConnectionRegistry registry,
    TranscriptWaiters waiters,
    ILogger<TranscriptRelayService> logger,
    TimeProvider? clock = null)
{
    /// <summary>
    /// How long one range may take before the read is abandoned. Generous next to a local
    /// file read plus two runner-channel hops, and bounded so a machine that is connected
    /// but wedged — or one running a <c>landbridged</c> that predates
    /// <see cref="ReadTranscriptCommand"/> and therefore rejects it at the wire boundary
    /// without replying — surfaces as a clear failure instead of a hung page.
    /// </summary>
    public static readonly TimeSpan RangeTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// One read at a time per machine. A transcript read is an operator convenience riding
    /// the same socket as dispatch and liveness, so two operators (or one refreshing) must
    /// not be able to multiply what it costs that machine.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perMachine = new(StringComparer.Ordinal);

    /// <summary>
    /// What <paramref name="machine"/> holds for <paramref name="task"/>: the captured
    /// instances and each stream's size, or why nothing can be listed.
    /// </summary>
    public Task<TranscriptResult> ListAsync(
        SessionId task, string machine, CancellationToken ct = default) =>
        AskAsync(task, machine, new ReadTranscriptCommand(task, NewRequestId(), Ordinal: 0), ct);

    /// <summary>
    /// One range of one captured stream. <paramref name="offset"/> is the cursor a previous
    /// reply returned (<see cref="TranscriptChunkEvent.NextOffset"/>); start at 0.
    /// </summary>
    public Task<TranscriptResult> ReadAsync(
        SessionId task, string machine, int ordinal, string stream, long offset,
        int maxBytes = TranscriptStreams.DefaultMaxBytes, CancellationToken ct = default) =>
        AskAsync(task, machine, new ReadTranscriptCommand(task, NewRequestId(), ordinal, stream, offset, maxBytes), ct);

    private async Task<TranscriptResult> AskAsync(
        SessionId task, string machine, ReadTranscriptCommand command, CancellationToken ct)
    {
        // The gate, before anything else: no state read of a machine, no command sent.
        using (var scope = scopes.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<SessionStore>();
            var state = await store.GetStateAsync(task, ct);
            if (state is null)
                return new TranscriptResult.Unavailable(
                    TranscriptUnavailable.NoSuchSession, "No such task.");
            if (!state.Value.IsTerminal())
            {
                return new TranscriptResult.Unavailable(
                    TranscriptUnavailable.NotTerminal,
                    $"This task is {Humanize(state.Value)}. A transcript becomes readable once the task " +
                    "reaches a terminal state; until then read it on the machine running it.");
            }
        }

        if (registry.SnapshotFor(machine) is null)
        {
            return new TranscriptResult.Unavailable(
                TranscriptUnavailable.MachineOffline,
                $"Machine '{machine}' is not connected. Transcripts are stored only on the machine that " +
                "captured them, so this one cannot be read until it reconnects.");
        }

        var gate = _perMachine.GetOrAdd(machine, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, ct))
        {
            return new TranscriptResult.Unavailable(
                TranscriptUnavailable.Busy,
                $"Another transcript read is already in flight to machine '{machine}'. Try again in a moment.");
        }

        try
        {
            using var waiter = waiters.Register(command.RequestId);

            // Best-effort against a live connection (§10): a machine that dropped between
            // the check above and here is the same answer as one that was never there.
            if (!await registry.SendAsync(machine, command, ct))
            {
                return new TranscriptResult.Unavailable(
                    TranscriptUnavailable.MachineOffline,
                    $"Machine '{machine}' could not be reached. Transcripts are stored only on the machine " +
                    "that captured them, so this one cannot be read until it reconnects.");
            }

            TranscriptChunkEvent reply;
            try
            {
                reply = await waiter.Reply.WaitAsync(RangeTimeout, _clock, ct);
            }
            catch (TimeoutException)
            {
                logger.LogInformation(
                    "transcript read {RequestId} for task {Task} timed out against machine {Machine}",
                    command.RequestId, task, machine);
                return new TranscriptResult.Unavailable(
                    TranscriptUnavailable.Timeout,
                    $"Machine '{machine}' did not answer within {RangeTimeout.TotalSeconds:N0}s. It may be " +
                    "wedged, or running a landbridged that predates transcript serving.");
            }

            return Interpret(reply, machine);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Maps the machine's reply onto a caller-facing result. The machine's refusal
    /// vocabulary is turned into operator-facing sentences here; the plane never invents a
    /// reason of its own.</summary>
    private static TranscriptResult Interpret(TranscriptChunkEvent reply, string machine) =>
        reply.Refusal switch
        {
            null when reply.Instances is { } instances => new TranscriptResult.Inventory(machine, instances),
            null => new TranscriptResult.Range(machine, reply.Text, reply.NextOffset, reply.Eof),
            TranscriptRefusals.NoTranscript => new TranscriptResult.Unavailable(
                TranscriptUnavailable.MachineRefused,
                $"Machine '{machine}' holds no transcript for this task. It may have run elsewhere, capture " +
                "may have been off for its profile, or the machine's retention window has already swept it."),
            TranscriptRefusals.UnknownInstance => new TranscriptResult.Unavailable(
                TranscriptUnavailable.MachineRefused,
                "That attempt or stream was never captured."),
            TranscriptRefusals.BadRequest => new TranscriptResult.Unavailable(
                TranscriptUnavailable.MachineRefused, "That transcript request was malformed."),
            TranscriptRefusals.ReadFailed => new TranscriptResult.Unavailable(
                TranscriptUnavailable.MachineRefused,
                $"Machine '{machine}' could not read the file (permissions, or it vanished mid-read)."),
            var unknown => new TranscriptResult.Unavailable(
                TranscriptUnavailable.MachineRefused, $"Machine '{machine}' refused the read: {unknown}."),
        };

    private static string Humanize(SessionState state) => state switch
    {
        SessionState.BlockedOnInput => "blocked on input",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string NewRequestId() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// Outcome of a transcript read (§12). A plain result rather than a
/// <see cref="StoreResult"/> — reading a transcript is not a task transition — so the
/// dashboard maps it to a status and a sentence.
/// </summary>
public abstract record TranscriptResult
{
    private TranscriptResult() { }

    /// <summary>What one machine holds for the task.</summary>
    public sealed record Inventory(string Machine, IReadOnlyList<TranscriptInstance> Instances) : TranscriptResult;

    /// <summary>One range of verbatim transcript text, with the cursor for the next read.</summary>
    public sealed record Range(string Machine, string Text, long NextOffset, bool Eof) : TranscriptResult;

    /// <summary>Nothing could be read; <see cref="Detail"/> is operator-facing.</summary>
    public sealed record Unavailable(TranscriptUnavailable Reason, string Detail) : TranscriptResult;
}

/// <summary>Why a transcript read produced nothing (§12) — each renders differently, because
/// "the machine is offline" and "this task is still running" are different problems with
/// different fixes, and neither may present as a hang.</summary>
public enum TranscriptUnavailable
{
    /// <summary>No task with that id.</summary>
    NoSuchSession,

    /// <summary>The task can still run again, so its transcript is not served (§12/§13).</summary>
    NotTerminal,

    /// <summary>The machine holding the bytes has no live connection.</summary>
    MachineOffline,

    /// <summary>The machine is connected but did not answer in time.</summary>
    Timeout,

    /// <summary>A read to that machine is already in flight.</summary>
    Busy,

    /// <summary>The machine answered with a refusal from the §12 vocabulary.</summary>
    MachineRefused,
}
