using Docket.ControlPlane.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Docket.ControlPlane;

/// <summary>DI wiring for the relay-forward orchestration (spec §8.3).</summary>
public static class ForwardingServiceCollectionExtensions
{
    /// <summary>
    /// Registers what a forward needs at both ends. The singletons drive the two
    /// docketd ends: the <see cref="RunnerConnectionRegistry"/> machines are resolved
    /// from, the <see cref="ForwardWaiters"/> the event sink completes, and the
    /// <see cref="ForwardOrchestrator"/> itself. The scoped
    /// <see cref="LeadMachineBindingService"/> resolves the consumer end of the
    /// <em>human</em> path — which enrolled machine a Lead bound as its own (§8.3) —
    /// and is also what <c>get_team_state</c> reads that binding back from.
    ///
    /// <para>All via <c>TryAdd</c> so a host that already registered the connection
    /// registry (every host wiring the runner spine does) keeps its instance — the
    /// orchestrator and the sink must share exactly that one.</para>
    /// </summary>
    public static IServiceCollection AddDocketForwarding(this IServiceCollection services)
    {
        services.TryAddSingleton<RunnerConnectionRegistry>();
        services.TryAddSingleton<ForwardWaiters>();
        services.TryAddSingleton<ForwardOrchestrator>();
        services.TryAddScoped<LeadMachineBindingService>();
        // §12 transcript serving rides the same runner channel and the same registry, so
        // its rendezvous and relay register here too — the sink and the relay must share
        // exactly one TranscriptWaiters, for the same reason forwards do.
        services.TryAddSingleton<TranscriptWaiters>();
        // §10 agent-started processes: the request/reply rendezvous for start/stop/write, and
        // the only place a worker's declaration reaches its machine.
        services.TryAddSingleton<ProcessControlRelay>();
        services.TryAddSingleton<TranscriptRelayService>();
        return services;
    }
}
