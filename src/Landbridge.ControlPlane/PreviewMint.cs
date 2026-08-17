using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// Shared mint policy for the two §8.4 preview mint surfaces (the worker
/// <c>open_preview</c> tool and the §12 dashboard button): the TTL rules and the
/// public preview URL, so both apply them identically.
/// </summary>
public static class PreviewMint
{
    /// <summary>Default life of a gated preview when the caller names no TTL.</summary>
    public static readonly TimeSpan GatedDefaultTtl = TimeSpan.FromHours(24);

    /// <summary>Default life of a public preview — short, because the label alone admits (§8.4).</summary>
    public static readonly TimeSpan PublicDefaultTtl = TimeSpan.FromMinutes(15);

    /// <summary>Hard ceiling for a public preview's TTL — public is always time-boxed (§8.4).</summary>
    public static readonly TimeSpan PublicMaxTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Resolve the mint TTL for <paramref name="policy"/> from an optional caller
    /// request (minutes). Public is clamped to <see cref="PublicMaxTtl"/> and
    /// defaults short; gated defaults long. A non-positive request is treated as
    /// "unspecified".
    /// </summary>
    public static TimeSpan ResolveTtl(PreviewAuthPolicy policy, int? requestedMinutes)
    {
        if (policy == PreviewAuthPolicy.Public)
        {
            var ttl = requestedMinutes is { } m and > 0 ? TimeSpan.FromMinutes(m) : PublicDefaultTtl;
            return ttl > PublicMaxTtl ? PublicMaxTtl : ttl; // public is mandatory-short (§8.4)
        }

        return requestedMinutes is { } g and > 0 ? TimeSpan.FromMinutes(g) : GatedDefaultTtl;
    }

    /// <summary>
    /// The shareable preview URL for <paramref name="label"/>: the opaque label as
    /// the leftmost subdomain of the configured wildcard base
    /// (<c>Docket:PreviewUrlBase</c>, e.g. <c>https://preview.example.com</c> or a
    /// dev <c>http://preview.localhost:5200</c>). Preserves scheme + any non-default
    /// port; never encodes structure beyond the single label (§8.4).
    /// </summary>
    public static string Url(string baseUrl, string label)
    {
        var uri = new Uri(baseUrl);
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme}://{label}.{uri.Host}{port}";
    }

    /// <summary>Config key for the wildcard preview base URL both mint surfaces read.</summary>
    public const string UrlBaseConfigKey = "Docket:PreviewUrlBase";
}
