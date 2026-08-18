using Microsoft.EntityFrameworkCore.Design;

namespace Landbridge.ControlPlane;

/// <summary>
/// Lets `dotnet ef` build the context at design time (migrations) with the
/// same Npgsql + snake_case options the runtime uses. The connection string is
/// only parsed for the provider, never opened, so any placeholder works.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LandbridgeDbContext>
{
    public LandbridgeDbContext CreateDbContext(string[] args) =>
        new(LandbridgeDbContext.BuildOptions(
            Environment.GetEnvironmentVariable("LANDBRIDGE_DB")
            ?? "Host=localhost;Database=landbridge;Username=landbridge"));
}
