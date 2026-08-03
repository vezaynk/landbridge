namespace Docket.Meta;

/// <summary>
/// docket-meta's configuration surface (config section <c>Meta</c>). Everything the
/// panel needs beyond the operator passphrase hash (<c>Meta:Operator:PassphraseHash</c>,
/// read directly by <see cref="Auth.MetaOperatorVerifier"/>) and the store
/// connection string (<c>ConnectionStrings:Meta</c> / <c>DOCKET_META_DB</c>).
/// </summary>
public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    /// <summary>
    /// The base domain instances are published under: an instance <c>foo</c> gets the
    /// MCP route <c>foo.docket.&lt;Domain&gt;</c> and the relay route
    /// <c>relay-foo.docket.&lt;Domain&gt;</c> (design note §4, deviation #2). Wildcard
    /// DNS + Caddy TLS automation for these are operator prerequisites (docs/META.md).
    /// </summary>
    public string Domain { get; set; } = "localhost";

    /// <summary>
    /// Image repositories (without tag) for the two per-instance containers. An
    /// instance records only a shared <see cref="Data.InstanceRow.ImageTag"/>; the
    /// full refs are <c>{McpImageRepo}:{tag}</c> and <c>{RelayImageRepo}:{tag}</c>,
    /// so image rollout (spec §3) moves both together (design note §3).
    /// </summary>
    public string McpImageRepo { get; set; } = "docket-mcp";
    public string RelayImageRepo { get; set; } = "docket-relay";

    /// <summary>The image tag a new instance pins by default (overridable per create).</summary>
    public string DefaultImageTag { get; set; } = "latest";

    /// <summary>The Postgres image every instance's private database container runs (matches the plane's CI).</summary>
    public string PostgresImage { get; set; } = "postgres:16";

    /// <summary>The edge Caddy admin API base (design note §4). Default is Caddy's own default.</summary>
    public string CaddyAdminUrl { get; set; } = "http://localhost:2019";

    /// <summary>The Caddy server key (inside <c>apps/http/servers</c>) routes are added to.</summary>
    public string CaddyServerName { get; set; } = "srv0";

    /// <summary>
    /// When true, meta applies its OWN checked-in migration to its OWN store at
    /// startup (dev convenience; mirrors the plane's Docket:MigrateOnStartup). This
    /// is meta's store, unrelated to the per-instance <c>Docket:MigrateOnStartup</c>
    /// meta injects into each instance container (deviation #4).
    /// </summary>
    public bool MigrateOnStartup { get; set; }
}
