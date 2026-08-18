using Landbridge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Landbridge.ControlPlane;

/// <summary>
/// The control-plane policy the write path stamps onto new tasks (§9 check 7). One knob
/// today: the ceiling on a task's infrastructure requeues, past which the task is
/// abandoned instead of redispatched forever (#73).
///
/// <para>A registered singleton rather than a constructor argument on
/// <see cref="SessionStore"/> because the store is resolved per scope; a
/// <see cref="SessionStore"/> built by hand (the pure store tests) gets none and falls back
/// to <see cref="SessionRecord.DefaultInfrastructureRequeueLimit"/>, so the default is the
/// behaviour everywhere it is not configured.</para>
/// </summary>
/// <param name="InfrastructureRequeueLimit">
/// The cap stamped onto each new task. Non-positive means uncapped — the pre-cap
/// behaviour, kept as a deliberate opt-out (§9 check 7).
/// </param>
public sealed record SessionStorePolicy(
    int InfrastructureRequeueLimit = SessionRecord.DefaultInfrastructureRequeueLimit);

/// <summary>
/// DI wiring for the task store and the accounting that is part of it, in the style of
/// <see cref="ForwardingServiceCollectionExtensions.AddLandbridgeForwarding"/>.
///
/// <para>One registration rather than two lines a host could get half-right: the write path
/// and the per-Team accounting §9.10 attributes through it belong to the same feature, and a
/// host that took the store alone would resolve fine and then fail only where a relay reported
/// bytes.</para>
///
/// <para><c>TryAdd</c> throughout, so a host or test that registered its own implementation
/// first still wins.</para>
/// </summary>
public static class StoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the §15 write path (<see cref="SessionStore"/>) together with §9.10's per-Team
    /// relay byte attribution (<see cref="TeamForwardUsageService"/>, which the relay's
    /// plane-facing usage report resolves). Scoped, matching the DbContext lifetime they both
    /// resolve against.
    /// </summary>
    /// <param name="infrastructureRequeueLimit">
    /// §9 check 7: the infrastructure requeue cap stamped onto new tasks, or null for
    /// <see cref="SessionRecord.DefaultInfrastructureRequeueLimit"/>. A host reads it from
    /// configuration (<c>Landbridge:InfrastructureRequeueLimit</c>); tests and fixtures leave
    /// it alone unless the cap is what they are exercising.
    /// </param>
    public static IServiceCollection AddLandbridgeStore(
        this IServiceCollection services, int? infrastructureRequeueLimit = null)
    {
        services.TryAddSingleton(new SessionStorePolicy(
            infrastructureRequeueLimit ?? SessionRecord.DefaultInfrastructureRequeueLimit));
        services.TryAddSingleton<LeadSetupLinkStore>();
        services.TryAddScoped<SessionStore>();
        services.TryAddScoped<TeamForwardUsageService>();
        return services;
    }
}
