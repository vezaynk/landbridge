using Docket.Meta.Data;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Provisioning;

/// <summary>What the create form supplies. HostId null = let placement choose (least loaded).</summary>
public sealed record CreateInstanceRequest(string Name, string? AccountLabel, string ImageTag, Guid? HostId);

/// <summary>
/// The create result. <see cref="Passphrase"/> is the operator passphrase in
/// plaintext — returned to the caller EXACTLY ONCE for the shown-once page and never
/// persisted (only its hash lands on the row). See design note §5.
/// </summary>
public sealed record CreateInstanceResult(Guid Id, string Name, string Passphrase);

/// <summary>Raised when create input is rejected (bad name, no host, duplicate name).</summary>
public sealed class InstanceCreateException(string message) : Exception(message);

/// <summary>
/// Allocates an Instance record and its credentials up front — the "secrets before
/// any side effect" rule (design note §2/§5): host placement, host-port allocation,
/// and all secret generation happen here, before a single Docker object exists.
/// Provisioning then only realizes what this recorded, so a crash-and-resume never
/// regenerates a credential and orphans a container.
///
/// <para>Port allocation is a read-then-insert, so it runs inside a transaction that
/// holds a per-host advisory lock — see <see cref="CreateAsync"/> for why the
/// invariant cannot be an index.</para>
/// </summary>
public sealed class InstanceCreator(
    MetaDbContext db,
    PlacementService placement,
    SecretGenerator secrets,
    MetaOptions options,
    TimeProvider clock)
{
    /// <summary>
    /// The message an operator meets when neither the create request nor
    /// <see cref="MetaOptions.DefaultImageTag"/> names a tag. It has to say where tags
    /// come from, because there is no sane fallback: <c>latest</c> is never published.
    /// </summary>
    internal const string NoTagMessage =
        "No image tag given, and Meta:DefaultImageTag is not set. Pin an explicit tag — " +
        "the publish-images workflow publishes a version tag (e.g. v0.4.0) and an immutable " +
        "sha-<commit> tag for each build; prefer the sha- tag. Nothing publishes 'latest'. " +
        "See docs/META.md §1.4.";

    /// <summary>
    /// Validates, places, allocates, and inserts — atomically with respect to another
    /// concurrent create on the same host.
    ///
    /// <para><b>Why a lock and not a unique index.</b> A published port lives in one of
    /// TWO columns (<c>mcp_published_port</c>, <c>relay_published_port</c>), so a
    /// per-column unique index cannot state the actual invariant — "no live instance on
    /// this host uses this port in EITHER role". It would still admit one instance's mcp
    /// port colliding with another's relay port. The invariant spans columns and rows, so
    /// it is enforced where the decision is made: a transaction-scoped advisory lock keyed
    /// on the host makes <see cref="PlacementService.AllocatePortsAsync"/>'s read-then-insert
    /// indivisible. (A normalized one-row-per-allocated-port table WOULD carry a real
    /// unique index, but that is a schema change earning nothing else here.)</para>
    /// </summary>
    public async Task<CreateInstanceResult> CreateAsync(CreateInstanceRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim().ToLowerInvariant();
        if (!InstanceNaming.IsValidName(name))
            throw new InstanceCreateException(
                "Name must be a DNS label: lowercase letters, digits, and hyphens (1–40 chars).");

        // Resolved here, with the other input validation, so a missing tag costs no side
        // effect — rejecting after port allocation would burn ports on a doomed create.
        var imageTag = string.IsNullOrWhiteSpace(req.ImageTag)
            ? (options.DefaultImageTag ?? "").Trim()
            : req.ImageTag.Trim();
        if (imageTag.Length == 0)
            throw new InstanceCreateException(NoTagMessage);

        // EF InMemory (the saga tests) supports neither transactions nor advisory locks;
        // those tests are single-threaded, so the unlocked path is equivalent there.
        await using var tx = db.Database.IsNpgsql()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        if (await db.Instances.AnyAsync(i => i.Name == name && i.DestroyedAt == null, ct))
            throw new InstanceCreateException($"An instance named '{name}' already exists.");

        var host = req.HostId is Guid hid
            ? await db.Hosts.FirstOrDefaultAsync(h => h.Id == hid && h.RemovedAt == null, ct)
                ?? throw new InstanceCreateException("The selected host does not exist.")
            : await placement.LeastLoadedHostAsync(ct)
                ?? throw new InstanceCreateException("No Docker hosts registered. Add a host first.");

        if (tx is not null)
        {
            // Released on commit or rollback, so a failed create never wedges the host.
            var key = AdvisoryKey(host.Id);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        }

        var (mcpPort, relayPort) = await placement.AllocatePortsAsync(host, ct);

        var passphrase = secrets.NewPassphrase();
        var instance = new InstanceRow
        {
            Id = Guid.NewGuid(),
            Name = name,
            AccountLabel = string.IsNullOrWhiteSpace(req.AccountLabel) ? null : req.AccountLabel.Trim(),
            HostId = host.Id,
            ImageTag = imageTag,
            State = InstanceState.Provisioning,
            McpPublishedPort = mcpPort,
            RelayPublishedPort = relayPort,
            PassphraseHash = SecretGenerator.Hash(passphrase),
            DbPassword = secrets.NewSecret(),
            RelayBearer = secrets.NewSecret(),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Instances.Add(instance);
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        // Plaintext returned, never stored.
        return new CreateInstanceResult(instance.Id, instance.Name, passphrase);
    }

    /// <summary>
    /// A stable 64-bit advisory-lock key for a host. Derived from the host id, so every
    /// meta process and request agrees on it without a registry; a collision between two
    /// hosts would only over-serialize their creates, never corrupt an allocation.
    /// </summary>
    private static long AdvisoryKey(Guid hostId) =>
        BitConverter.ToInt64(hostId.ToByteArray(), 0);
}
