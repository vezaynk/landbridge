namespace Docket.Meta.Edge;

/// <summary>
/// The edge router seam (design note §4). Meta owns adding/removing an Instance's
/// public routes on a single edge Caddy, live via its admin API. Each route carries
/// a stable <c>@id</c> so add is an upsert and remove is precise — the panel never
/// leaves a dangling or duplicate route. A fake stands in for unit tests; a real
/// <c>caddy:2</c> container backs the Docker E2E (design note §7).
/// </summary>
public interface ICaddyAdmin
{
    /// <summary>Liveness probe for the admin API (host-registry/E2E readiness).</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>True iff a route with this <c>@id</c> is currently configured.</summary>
    Task<bool> HasRouteAsync(string routeId, CancellationToken ct);

    /// <summary>
    /// Adds (or replaces, if the <c>@id</c> already exists) a host route that
    /// reverse-proxies <paramref name="host"/> to <paramref name="upstream"/>
    /// (<c>ip:port</c>). Idempotent.
    /// </summary>
    Task UpsertRouteAsync(string routeId, string host, string upstream, CancellationToken ct);

    /// <summary>Removes the route with this <c>@id</c>. No-op if it is already gone.</summary>
    Task RemoveRouteAsync(string routeId, CancellationToken ct);
}
