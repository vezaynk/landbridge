using Docket.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Docket.ControlPlane;

/// <summary>
/// The control-plane policy the write path stamps onto new tasks (§9 check 7). One knob
/// today: the ceiling on a task's infrastructure requeues, past which the task is
/// abandoned instead of redispatched forever (#73).
///
/// <para>A registered singleton rather than a constructor argument on
/// <see cref="TaskStore"/> because the store is resolved per scope; a
/// <see cref="TaskStore"/> built by hand (the pure store tests) gets none and falls back
/// to <see cref="TaskRecord.DefaultInfrastructureRequeueLimit"/>, so the default is the
/// behaviour everywhere it is not configured.</para>
/// </summary>
/// <param name="InfrastructureRequeueLimit">
/// The cap stamped onto each new task. Non-positive means uncapped — the pre-cap
/// behaviour, kept as a deliberate opt-out (§9 check 7).
/// </param>
public sealed record TaskStorePolicy(
    int InfrastructureRequeueLimit = TaskRecord.DefaultInfrastructureRequeueLimit);

/// <summary>
/// DI wiring for the task store and the accounting that is part of it, in the style of
/// <see cref="ForwardingServiceCollectionExtensions.AddDocketForwarding"/>.
///
/// <para>These two are one registration because <see cref="TaskStore"/> takes
/// <see cref="TeamBudgetService"/> as an <em>optional</em> constructor dependency (§9.9): the
/// dispatch path commits a Team's per-dispatch cap through it, and a host that registered the
/// store alone would get a store that silently commits nothing and hands every harness a null
/// cap — a budget ceiling that looks configured and enforces nothing. That failure is invisible
/// at startup and invisible in every test that does not assert on committed amounts, which is
/// exactly the kind of mistake a host should not be able to make one line at a time.</para>
///
/// <para><c>TryAdd</c> throughout, so a host or test that registered its own implementation
/// first still wins.</para>
/// </summary>
public static class StoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the §15 write path (<see cref="TaskStore"/>) together with the accounting it
    /// commits through: §9.9's budget ceiling (<see cref="TeamBudgetService"/>) and §9.10's
    /// per-Team relay byte attribution (<see cref="TeamForwardUsageService"/>, which the
    /// relay's plane-facing usage report resolves). Scoped, matching the DbContext lifetime
    /// they all resolve against.
    /// </summary>
    /// <param name="infrastructureRequeueLimit">
    /// §9 check 7: the infrastructure requeue cap stamped onto new tasks, or null for
    /// <see cref="TaskRecord.DefaultInfrastructureRequeueLimit"/>. A host reads it from
    /// configuration (<c>Docket:InfrastructureRequeueLimit</c>); tests and fixtures leave
    /// it alone unless the cap is what they are exercising.
    /// </param>
    public static IServiceCollection AddDocketStore(
        this IServiceCollection services, int? infrastructureRequeueLimit = null)
    {
        services.TryAddSingleton(new TaskStorePolicy(
            infrastructureRequeueLimit ?? TaskRecord.DefaultInfrastructureRequeueLimit));
        services.TryAddScoped<TaskStore>();
        services.TryAddScoped<TeamBudgetService>();
        services.TryAddScoped<TeamForwardUsageService>();
        return services;
    }
}
