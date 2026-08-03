using Microsoft.EntityFrameworkCore.Design;

namespace Docket.Meta.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the context at design time (migrations) with the
/// same Npgsql + snake_case options the runtime uses, mirroring the plane's
/// factory. The connection string is only parsed for the provider, never opened.
/// </summary>
public sealed class MetaDesignTimeDbContextFactory : IDesignTimeDbContextFactory<MetaDbContext>
{
    public MetaDbContext CreateDbContext(string[] args) =>
        new(MetaDbContext.BuildOptions(
            Environment.GetEnvironmentVariable("DOCKET_META_DB")
            ?? "Host=localhost;Database=docket_meta;Username=docket"));
}
