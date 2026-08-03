using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Docket.ControlPlane;

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
    public static IServiceCollection AddDocketStore(this IServiceCollection services)
    {
        services.TryAddScoped<TaskStore>();
        services.TryAddScoped<TeamBudgetService>();
        services.TryAddScoped<TeamForwardUsageService>();
        return services;
    }
}
