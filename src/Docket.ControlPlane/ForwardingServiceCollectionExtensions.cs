using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Docket.ControlPlane;

/// <summary>DI wiring for the relay-forward orchestration (spec §8.3).</summary>
public static class ForwardingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the singletons <c>open_forward</c> needs to drive the two
    /// docketd ends of a forward: the <see cref="RunnerConnectionRegistry"/> it
    /// resolves machines from, the <see cref="ForwardWaiters"/> the event sink
    /// completes, and the <see cref="ForwardOrchestrator"/> itself. All via
    /// <c>TryAdd</c> so a host that already registered the connection registry
    /// (every host wiring the runner spine does) keeps its instance — the
    /// orchestrator and the sink must share exactly that one.
    /// </summary>
    public static IServiceCollection AddDocketForwarding(this IServiceCollection services)
    {
        services.TryAddSingleton<RunnerConnectionRegistry>();
        services.TryAddSingleton<ForwardWaiters>();
        services.TryAddSingleton<ForwardOrchestrator>();
        // §12 transcript serving rides the same runner channel and the same registry, so
        // its rendezvous and relay register here too — the sink and the relay must share
        // exactly one TranscriptWaiters, for the same reason forwards do.
        services.TryAddSingleton<TranscriptWaiters>();
        services.TryAddSingleton<TranscriptRelayService>();
        return services;
    }
}
