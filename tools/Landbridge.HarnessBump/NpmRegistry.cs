using System.Net.Http.Headers;
using System.Text.Json;

namespace Landbridge.HarnessBump;

/// <summary>Reads published versions from the public npm registry.</summary>
public sealed class NpmRegistry(HttpClient http)
{
    /// <summary>
    /// The abbreviated ("install") packument. Asking for it is not just a size win — the full
    /// document for <c>@anthropic-ai/claude-code</c> is tens of megabytes of per-version
    /// metadata, and the abbreviated form still carries <c>dist-tags</c>, which is all this
    /// tool reads. It also omits <c>time</c>, which is exactly the field a newest-by-timestamp
    /// implementation would reach for and get wrong.
    /// </summary>
    private const string AbbreviatedPackument = "application/vnd.npm.install-v1+json";

    public async Task<string> GetLatestAsync(string package, CancellationToken cancellationToken)
    {
        // The scope separator must be escaped — registry.npmjs.org/@openai/codex 404s,
        // registry.npmjs.org/@openai%2Fcodex does not.
        var path = package.Replace("/", "%2F", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://registry.npmjs.org/{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AbbreviatedPackument));

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ReadLatestDistTag(json, package);
    }

    /// <summary>
    /// Pulls <c>dist-tags.latest</c> out of a packument.
    /// </summary>
    /// <remarks>
    /// <c>latest</c> specifically, and nothing else, is the contract. Two of the three
    /// harnesses publish a tag that is newer by publish time but must never be installed:
    /// <c>@openai/codex</c> keeps an <c>alpha</c> tag (<c>0.148.0-alpha.12</c>) and
    /// <c>opencode-ai</c> keeps <c>dev</c>/<c>beta</c>/<c>snapshot-*</c> tags pointing at
    /// <c>0.0.0-&lt;branch&gt;-&lt;stamp&gt;</c> builds. Sorting versions by publish date, or
    /// taking the max over <c>dist-tags</c>, picks one of those.
    /// </remarks>
    public static string ReadLatestDistTag(string packumentJson, string package)
    {
        using var document = JsonDocument.Parse(packumentJson);
        if (!document.RootElement.TryGetProperty("dist-tags", out var distTags) ||
            distTags.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{package}: registry response has no dist-tags object.");
        if (!distTags.TryGetProperty("latest", out var latest) ||
            latest.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{package}: registry response has no dist-tags.latest string.");

        var version = latest.GetString();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException($"{package}: dist-tags.latest is empty.");
        return version;
    }
}
