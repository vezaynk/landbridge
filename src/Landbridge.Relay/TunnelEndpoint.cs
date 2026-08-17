using System.Net.WebSockets;
using Docket.Contracts;
using Microsoft.Extensions.Options;

namespace Docket.Relay;

/// <summary>
/// The relay's single tunnel endpoint (spec §8.3). A connecting party upgrades
/// to a WebSocket and presents, via request headers, the forward id that pairs
/// the two ends, an opaque grant, and its role. The relay validates the grant,
/// pairs the two ends by forward id, and splices their byte streams. It treats
/// every payload as opaque bytes — it moves bytes without interpreting them
/// (design principle 1). A thin transport shell: pairing/teardown lives in
/// <see cref="ForwardRegistry"/> and <see cref="ForwardEntry"/>, grant policy in
/// <see cref="IGrantValidator"/>.
///
/// <para>Transport is a WebSocket (binary frames): a real HTTP upgrade, so it is
/// spec-compatible, and it preserves byte-stream order because it rides TCP.</para>
/// </summary>
public static class TunnelEndpoint
{
    // Header names + role strings live in Docket.Contracts (RelayTunnel) so the
    // runner's tunnel client presents exactly what this endpoint reads (§8.3).

    public static void MapRelayTunnel(this WebApplication app) =>
        app.Map("/tunnel", HandleAsync);

    private static async Task HandleAsync(
        HttpContext context,
        ForwardRegistry registry,
        IGrantValidator grants,
        IOptions<RelayOptions> options,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Docket.Relay.Tunnel");
        var ct = context.RequestAborted;

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var forwardId = context.Request.Headers[RelayTunnel.ForwardIdHeader].ToString();
        var grant = context.Request.Headers[RelayTunnel.GrantHeader].ToString();
        var roleText = context.Request.Headers[RelayTunnel.RoleHeader].ToString();

        if (string.IsNullOrEmpty(forwardId) || string.IsNullOrEmpty(grant)
            || !TryParseRole(roleText, out var role))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // The grant is checked BEFORE pairing/splicing (spec §8.3). Both ends
        // authenticate to the relay independently; neither authenticates to the
        // other. An invalid grant is refused with no upgrade and no pairing.
        if (!await grants.ValidateAsync(grant, forwardId, role, ct))
        {
            logger.LogInformation(
                "tunnel refused: invalid grant for forward {ForwardId} role {Role}", forwardId, role);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Claim the role slot BEFORE upgrading, so a duplicate role for the same
        // forward id is refused with a clean status and never occupies a socket.
        if (!registry.TryJoin(forwardId, role, out var entry))
        {
            logger.LogInformation(
                "tunnel refused: duplicate {Role} for forward {ForwardId}", role, forwardId);
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            entry.SetSocket(role, socket);
            logger.LogInformation("tunnel open: {Role} for forward {ForwardId}", role, forwardId);

            if (!await entry.WaitForPairAsync(options.Value.PairWaitTimeout, ct))
            {
                logger.LogInformation(
                    "tunnel unpaired: {Role} for forward {ForwardId} timed out waiting for its pair",
                    role, forwardId);
                await RelaySocket.CloseOutputQuietlyAsync(socket, "relay: no pair within timeout");
                return;
            }

            await entry.SpliceAsync(ct);
            logger.LogInformation("tunnel closed: forward {ForwardId} spliced end", forwardId);
        }
        finally
        {
            // Clean removal on any close/error — a claimed-but-failed accept, a
            // timed-out wait, or a completed splice — so the forward id can never
            // leak and can be reused immediately.
            registry.Release(forwardId, entry);
        }
    }

    private static bool TryParseRole(string value, out RelayRole role)
    {
        if (string.Equals(value, RelayTunnel.ConsumerRole, StringComparison.OrdinalIgnoreCase))
        {
            role = RelayRole.Consumer;
            return true;
        }

        if (string.Equals(value, RelayTunnel.ProducerRole, StringComparison.OrdinalIgnoreCase))
        {
            role = RelayRole.Producer;
            return true;
        }

        role = default;
        return false;
    }
}
