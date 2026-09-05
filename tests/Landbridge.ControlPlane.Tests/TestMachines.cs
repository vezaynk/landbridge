using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// Enroll + socket + heartbeat columns. Test machine ids are real
/// <c>machines.id</c> values — no registry overlay.
/// </summary>
internal static class TestMachines
{
    public static async Task<Guid> EnrollAsync(LandbridgeDbContext db, TimeProvider clock, string name)
    {
        var tokens = new TokenService(db, clock);
        var enrollment = await tokens.IssueEnrollmentTokenAsync();
        var credentials = await tokens.ExchangeEnrollmentAsync(
            enrollment.Token, new MachineDeclaration(name, "linux"));
        return credentials!.MachineId;
    }

    public static RunnerConnectionRegistry.Registration Register(
        RunnerConnectionRegistry registry, Guid machineId,
        Func<RunnerCommand, CancellationToken, Task>? send = null) =>
        registry.Register(
            machineId.ToString(),
            new HashSet<string>(StringComparer.Ordinal),
            send ?? ((_, _) => Task.CompletedTask));

    public static async Task HeartbeatAsync(
        LandbridgeDbContext db, TimeProvider clock, Guid machineId,
        bool ready = true, bool underBackPressure = false,
        IReadOnlyList<string>? profiles = null,
        IReadOnlyList<ProcessStatus>? processes = null,
        CancellationToken ct = default)
    {
        var id = machineId.ToString();
        var beat = new MachineHeartbeat(
            id, ready, underBackPressure, default, 0,
            profiles ?? ["default"], clock.GetUtcNow(), Processes: processes);

        await HubOutbox.WriteHeartbeatAsync(db, clock, id, beat, ct);
    }

    public static async Task<Guid> ConnectAsync(
        LandbridgeDbContext db, TimeProvider clock, RunnerConnectionRegistry registry,
        string name, Func<RunnerCommand, CancellationToken, Task>? send = null,
        bool ready = true, bool underBackPressure = false,
        IReadOnlyList<string>? profiles = null, CancellationToken ct = default)
    {
        var id = await EnrollAsync(db, clock, name);
        Register(registry, id, send);
        await HeartbeatAsync(db, clock, id, ready, underBackPressure, profiles, ct: ct);
        return id;
    }
}
