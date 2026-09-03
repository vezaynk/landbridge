using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Landbridge.ControlPlane;

/// <summary>
/// Allocated <c>adjective-noun-NNNN</c> labels for dashboard addressing.
/// Uniqueness is the unique index plus insert retry, not the generator.
/// Guids stay the PK; these are human-facing aliases, not capabilities.
/// </summary>
public static class HaikuSlug
{
    public const string SessionsIndex = "ix_sessions_slug";
    public const string TeamsIndex = "ix_lead_teams_slug";
    public const string MachinesIndex = "ix_machines_slug";
    public const int RetryLimit = 8;

    public static string Mint()
    {
        var adj = RandomNumberGenerator.GetInt32(Adjectives.Length);
        var noun = RandomNumberGenerator.GetInt32(Nouns.Length);
        var n = RandomNumberGenerator.GetInt32(10_000);
        return $"{Adjectives[adj]}-{Nouns[noun]}-{n:D4}";
    }

    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        var first = value.IndexOf('-');
        if (first <= 0)
            return false;
        var second = value.IndexOf('-', first + 1);
        if (second < 0 || value.IndexOf('-', second + 1) >= 0)
            return false;
        if (second - first < 2)
            return false;
        if (value.Length - second != 5)
            return false;
        for (var i = 0; i < first; i++)
        {
            if (!char.IsAsciiLetterLower(value[i]))
                return false;
        }
        for (var i = first + 1; i < second; i++)
        {
            if (!char.IsAsciiLetterLower(value[i]))
                return false;
        }
        for (var i = second + 1; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
                return false;
        }
        return true;
    }

    public static bool IsConflict(DbUpdateException ex, string indexName) =>
        ex.InnerException is PostgresException { SqlState: "23505" } pg
        && string.Equals(pg.ConstraintName, indexName, StringComparison.Ordinal);

    /// <summary>
    /// Pick a slug that is not currently stored. The unique index is still the
    /// race backstop — callers retry on <see cref="IsConflict"/>.
    /// </summary>
    public static async Task<string> AllocateAsync(
        Func<string, CancellationToken, Task<bool>> taken, CancellationToken ct)
    {
        for (var i = 0; i < RetryLimit; i++)
        {
            var slug = Mint();
            if (!await taken(slug, ct).ConfigureAwait(false))
                return slug;
        }
        return Mint();
    }

    /// <summary>
    /// Turns migration placeholders (<c>row-mach-NNNN</c>) into allocated haiku.
    /// Idempotent. Called after <c>MigrateAsync</c> in the Aspire loop.
    /// </summary>
    public static async Task ReplaceMachinePlaceholdersAsync(
        LandbridgeDbContext db, CancellationToken ct = default)
    {
        var pending = await db.Set<Landbridge.ControlPlane.Auth.MachineRow>()
            .Where(m => m.Slug == "" || m.Slug.StartsWith("row-mach-"))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var machine in pending)
        {
            machine.Slug = await AllocateAsync(
                (slug, token) => db.Set<Landbridge.ControlPlane.Auth.MachineRow>()
                    .AnyAsync(m => m.Slug == slug && m.Id != machine.Id, token),
                ct).ConfigureAwait(false);
        }
        if (pending.Count > 0)
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // Heroku-style haikunate lists. Enough pairs that a dashboard of sessions
    // will not collide in practice; the unique index is what holds.
    internal static readonly string[] Adjectives =
    [
        "aged", "ancient", "autumn", "billowing", "bitter", "black", "blue", "bold",
        "brave", "brief", "bright", "broken", "calm", "cold", "cool", "crimson",
        "damp", "dark", "dawn", "delicate", "divine", "dry", "empty", "falling",
        "floral", "fragrant", "frosty", "gentle", "green", "hidden", "holy", "icy",
        "late", "lingering", "little", "lively", "long", "misty", "morning", "muddy",
        "nameless", "old", "patient", "polished", "proud", "purple", "quiet", "red",
        "restless", "rough", "shy", "silent", "small", "snowy", "solitary", "sparkling",
        "spring", "still", "summer", "twilight", "wandering", "weathered", "white",
        "wild", "winter", "wispy", "withered", "young",
    ];

    internal static readonly string[] Nouns =
    [
        "bird", "breeze", "brook", "bush", "butterfly", "cloud", "darkness", "dawn",
        "dew", "dream", "dust", "feather", "field", "fire", "firefly", "flower",
        "fog", "forest", "frog", "frost", "glade", "glitter", "grass", "haze",
        "hill", "lake", "leaf", "meadow", "moon", "morning", "mountain", "night",
        "paper", "pine", "pond", "rain", "resonance", "river", "sea", "shadow",
        "shape", "silence", "sky", "smoke", "snow", "snowflake", "sound", "star",
        "sun", "sunset", "surf", "thunder", "tree", "violet", "voice", "water",
        "waterfall", "wave", "wildflower", "wind", "wood",
    ];
}
