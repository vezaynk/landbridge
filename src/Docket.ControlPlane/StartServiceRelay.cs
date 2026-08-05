using System.Collections.Concurrent;
using Docket.Contracts;
using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// Relays a worker's <c>start_service</c> to the machine holding its task and waits for the
/// machine's answer (§10 agent-initiated services). The same request/reply shape as the §12
/// transcript read: register a waiter by request id, send, park on it, and let the
/// <see cref="RunnerEventSink"/> complete it when the reply lands.
///
/// <para><b>The plane decides nothing here.</b> Whether a task may start a service is its
/// profile's policy, and a profile's meaning is machine-local data the control plane never
/// learns (§10) — so this resolves which machine holds the task, forwards the request
/// verbatim, and reports back whatever the machine said. Every refusal in the result came
/// from the runner.</para>
/// </summary>
public sealed class StartServiceRelay(RunnerConnectionRegistry registry)
{
    /// <summary>How long a machine has to answer. Generous relative to a transcript range,
    /// because the runner starts the process and waits for readiness before replying — the
    /// worker needs the port to be answering before it registers (§8.2).</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ServiceStartedEvent>> _waiters =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Hand a machine's reply to whoever is waiting. Never blocks: the sink runs on the
    /// runner socket's receive loop, so anything awaited here delays every other inbound
    /// frame. A reply nobody waits for is dropped.
    /// </summary>
    public bool Complete(ServiceStartedEvent reply) =>
        _waiters.TryGetValue(reply.RequestId, out var tcs) && tcs.TrySetResult(reply);

    /// <summary>
    /// Asks the machine holding <paramref name="task"/> to start a service for it.
    /// </summary>
    public async Task<ServiceStartResult> StartAsync(
        TaskId task,
        string name,
        IReadOnlyList<string> spawn,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? env,
        int? port,
        int? readinessTcpPort,
        double? readinessTimeoutSeconds,
        CancellationToken ct)
    {
        var machine = registry.MachineFor(task);
        if (machine is null)
        {
            // No live lease means no machine to own the process. Refusing is the only honest
            // answer: a service has to belong to a task that is actually somewhere.
            return new ServiceStartResult(
                false, null, null,
                "this task is not currently held by any machine, so there is nowhere to start a service");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ServiceStartedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[requestId] = tcs;
        try
        {
            var sent = await registry.SendAsync(
                machine,
                new StartServiceCommand(
                    task, requestId, name, spawn, workingDirectory, env,
                    port, readinessTcpPort, readinessTimeoutSeconds),
                ct);
            if (!sent)
                return new ServiceStartResult(false, null, null, "the machine holding this task is unreachable");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            ServiceStartedEvent reply;
            try
            {
                reply = await tcs.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ServiceStartResult(
                    false, null, null,
                    $"the machine did not answer within {Timeout.TotalSeconds:0}s — the service may still be starting");
            }

            return new ServiceStartResult(reply.Started, reply.Port, reply.LogPath, reply.Refusal);
        }
        finally
        {
            _waiters.TryRemove(requestId, out _);
        }
    }
}

/// <summary>
/// What a worker learns from <c>start_service</c>. <see cref="Port"/> is null for a
/// port-less service (a watcher, an indexer) — there is nothing to register or forward to.
/// <see cref="LogPath"/> is where the machine captured this run, so the declaring agent can
/// read its own service's output with ordinary file tools.
/// </summary>
public sealed record ServiceStartResult(bool Started, int? Port, string? LogPath, string? Refusal);
