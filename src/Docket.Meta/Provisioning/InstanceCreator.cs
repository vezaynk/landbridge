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
/// and all secret generation happen here, inside one transaction, before a single
/// Docker object exists. Provisioning then only realizes what this recorded, so a
/// crash-and-resume never regenerates a credential and orphans a container.
/// </summary>
public sealed class InstanceCreator(
    MetaDbContext db,
    PlacementService placement,
    SecretGenerator secrets,
    TimeProvider clock)
{
    public async Task<CreateInstanceResult> CreateAsync(CreateInstanceRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim().ToLowerInvariant();
        if (!InstanceNaming.IsValidName(name))
            throw new InstanceCreateException(
                "Name must be a DNS label: lowercase letters, digits, and hyphens (1–40 chars).");

        if (await db.Instances.AnyAsync(i => i.Name == name && i.DestroyedAt == null, ct))
            throw new InstanceCreateException($"An instance named '{name}' already exists.");

        var host = req.HostId is Guid hid
            ? await db.Hosts.FirstOrDefaultAsync(h => h.Id == hid && h.RemovedAt == null, ct)
                ?? throw new InstanceCreateException("The selected host does not exist.")
            : await placement.LeastLoadedHostAsync(ct)
                ?? throw new InstanceCreateException("No Docker hosts registered. Add a host first.");

        var (mcpPort, relayPort) = await placement.AllocatePortsAsync(host, ct);

        var passphrase = secrets.NewPassphrase();
        var instance = new InstanceRow
        {
            Id = Guid.NewGuid(),
            Name = name,
            AccountLabel = string.IsNullOrWhiteSpace(req.AccountLabel) ? null : req.AccountLabel.Trim(),
            HostId = host.Id,
            ImageTag = string.IsNullOrWhiteSpace(req.ImageTag) ? "latest" : req.ImageTag.Trim(),
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

        // Plaintext returned, never stored.
        return new CreateInstanceResult(instance.Id, instance.Name, passphrase);
    }
}
