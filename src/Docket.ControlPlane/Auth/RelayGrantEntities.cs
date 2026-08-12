using Docket.Core;

namespace Docket.ControlPlane.Auth;

/// <summary>
/// Which end of a forward a grant is being presented for (spec §8.3). Mirrors
/// the relay's own role enum, but kept as the control plane's own type so the
/// plane never takes a dependency on <c>Docket.Relay</c> — the two agree only on
/// the wire strings <c>consumer</c>/<c>producer</c>.
/// </summary>
public enum RelayGrantRole
{
    Consumer,
    Producer,
}

/// <summary>
/// One forward grant (spec §8.3): a short-lived, opaque connection-establishment
/// credential bound to <c>{consumer, service, expiry}</c>, issued when a consumer
/// calls <c>open_forward</c> and checked once when each end opens its tunnel.
///
/// <para>A grant is <b>not</b> a <see cref="CredentialRow"/> / Principal — it
/// authenticates nothing to the MCP surface. It lives in its own table with its
/// own <c>dkt_g_</c> prefix, and only its SHA-256 lands here, exactly like the
/// opaque tokens of §5. It is single-use <em>per role</em>: the consumer opens
/// its tunnel once and the producer opens its tunnel once, so each side has its
/// own "used" stamp and a replay of either side is refused.</para>
///
/// <para>An issued grant is revoked when the owning (producer) task leaves
/// <c>working</c> — the same <see cref="ClearServicesAndForwards"/> effect that
/// clears the task's registered services (§6). Expiry gates only tunnel-open; an
/// established splice persists regardless (§8.3), which holds trivially because a
/// grant is validated only at open.</para>
///
/// <para><b>One exception, and it matters here more than anywhere.</b> A producer
/// blocked on a <b>permission</b> request (§11 permission bridge) does not emit that
/// effect — it is still alive inside its tool call — so its grants stay live, and they
/// stay live through a subsequent park or requeue as well, because the effect is only
/// emitted from <c>working</c>. A grant outliving its producer's <c>working</c> state is
/// therefore possible; expiry, not revocation, is what bounds it in that case.</para>
/// </summary>
public sealed class RelayGrantRow
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the opaque grant string; the grant itself is never stored (§5).</summary>
    public string GrantHash { get; set; } = "";

    /// <summary>The forward id that pairs the two tunnel ends. One grant, one forward.</summary>
    public Guid ForwardId { get; set; }

    // Who this grant was issued to (§8.3: bound to the consumer). Nullable because
    // an §8.4 HTTP-preview consumer is the preview frontend, not a task — the mint
    // records the producer + Team but has no consumer worker instance to bind. A
    // forward grant (§8.3) always sets both; a preview grant leaves both null.
    // Neither validation (ValidateAsync) nor revocation (ClearServicesAndForwards,
    // keyed on ProducerTaskId) reads these, so binding is attribution only.
    public Guid? ConsumerTaskId { get; set; }
    public Guid? ConsumerInstanceId { get; set; }

    /// <summary>The registered service name the forward resolves to (§8.2).</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>The task that registered the service — the forward's producer end (§8.3).</summary>
    public Guid ProducerTaskId { get; set; }

    public Guid TeamId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gates tunnel-open only; never severs an established splice (§8.3).</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set the first time the consumer end opens its tunnel; a second consumer open is refused.</summary>
    public DateTimeOffset? UsedByConsumerAt { get; set; }

    /// <summary>Set the first time the producer end opens its tunnel; a second producer open is refused.</summary>
    public DateTimeOffset? UsedByProducerAt { get; set; }

    /// <summary>Revoked when the producer task leaves working (ClearServicesAndForwards, §6/§8.3),
    /// except where a permission block keeps that effect from firing — see the type remarks.</summary>
    public bool Revoked { get; set; }
}

/// <summary>
/// Outcome of <see cref="RelayGrantService.IssueAsync"/>. A separate result type
/// (not <see cref="StoreResult"/>) because a grant is not a task transition; the
/// refusal still names a <see cref="Rule"/> so the tool surface reports the same
/// spec-traceable reason strings as every other refusal.
/// </summary>
public abstract record RelayGrantResult
{
    private RelayGrantResult() { }

    /// <summary>
    /// The grant was minted. The plaintext exists only in this value, once.
    /// <see cref="Producer"/> is the working task that registered the service —
    /// the plane resolves its machine to send the producer-side <c>open-forward</c>
    /// — and <see cref="Port"/> is that service's loopback port for the producer to
    /// dial (§8.3). Both are already known from the issuance lookup, so they ride
    /// the result rather than being re-queried by the orchestrator.
    /// </summary>
    public sealed record Issued(
        string Grant, Guid ForwardId, DateTimeOffset ExpiresAt, TaskId Producer, int Port) : RelayGrantResult;

    /// <summary>
    /// Refused (§9 check 11 — forwards resolve only to registered services owned
    /// by a working task in the same Team). A cross-Team request reads as
    /// not-registered by construction: the lookup is scoped to the caller's Team,
    /// so another Team's service names never leak (§8.2).
    /// </summary>
    public sealed record Refused(Rule Rule, string Reason) : RelayGrantResult;
}
