using Microsoft.EntityFrameworkCore.Design;

namespace Docket.ControlPlane;

/// <summary>
/// Lets `dotnet ef` build the context at design time (migrations) with the
/// same Npgsql + snake_case options the runtime uses. The connection string is
/// only parsed for the provider, never opened, so any placeholder works.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DocketDbContext>
{
    public DocketDbContext CreateDbContext(string[] args) =>
        new(DocketDbContext.BuildOptions(
            Environment.GetEnvironmentVariable("DOCKET_DB")
            ?? "Host=localhost;Database=docket;Username=docket"));
}
