using System.Text.Json;

namespace Docket.Meta.Substrate;

/// <summary>
/// A pull that could not be satisfied, translated into something an operator can act on.
///
/// <para>The raw Docker error is not usable as-is. A denied GHCR pull arrives as
/// <c>status code=InternalServerError, response={"message":"error from registry:
/// denied\ndenied"}</c> — no image name, no cause, and an HTTP status that suggests a
/// server fault rather than a missing credential. Since
/// <see cref="ISubstrate.EnsureImageAsync"/> runs inside the saga's first step, that
/// string is what gets persisted as the step error and rendered on the instance detail
/// page, so it is the whole of what an operator sees when provisioning stalls.</para>
///
/// <para>Deliberately free of any Docker.DotNet type: the status code and response body
/// come in as plain values, so the classification is unit-testable without a daemon and
/// the substrate seam stays Docker-agnostic.</para>
/// </summary>
public sealed class ImagePullException : Exception
{
    /// <summary>Where the operator guide explains publishing, pinning, and private packages.</summary>
    private const string Docs = "docs/META.md §1.4";

    public ImagePullException(string image, int? statusCode, string? responseBody, Exception? inner = null)
        : base(Explain(image, statusCode, responseBody), inner)
    {
        Image = image;
        RegistryDetail = Detail(responseBody);
    }

    /// <summary>The image reference that could not be pulled, tag included.</summary>
    public string Image { get; }

    /// <summary>The registry's own words, unwrapped from the JSON envelope and flattened to one line.</summary>
    public string RegistryDetail { get; }

    private static string Explain(string image, int? statusCode, string? responseBody)
    {
        var detail = Detail(responseBody);
        var lower = detail.ToLowerInvariant();

        // A registry answers "denied" both for a private repository pulled without
        // credentials and for one that does not exist — the two are indistinguishable from
        // here, so the message must own that ambiguity instead of guessing.
        if (lower.Contains("denied") || lower.Contains("unauthorized") || lower.Contains("authentication required"))
            return $"cannot pull image '{image}': the registry denied access. Either that tag is not " +
                   $"published or the package is private — a registry reports both the same way. meta pulls " +
                   $"anonymously and has no registry-credential setting, so a private package must be made " +
                   $"public or pre-pulled on this host ({Docs}). Registry said: {detail}";

        if (statusCode == 404 || lower.Contains("not found"))
            return $"cannot pull image '{image}': the registry has no such tag. Publish it — the " +
                   $"publish-images workflow tags every build with a version and an immutable sha-<commit> — " +
                   $"or pin a tag that exists ({Docs}). Registry said: {detail}";

        return $"cannot pull image '{image}': {detail} ({Docs})";
    }

    /// <summary>
    /// The readable core of a Docker error body. The daemon wraps its message as
    /// <c>{"message":"…"}</c> and embeds newlines; both would render badly in a step log,
    /// which is a single line of text.
    /// </summary>
    private static string Detail(string? responseBody)
    {
        var body = (responseBody ?? "").Trim();
        if (body.Length == 0)
            return "no detail reported";

        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { } s)
                    body = s;
            }
            catch (JsonException)
            {
                // Not JSON after all — fall through and use the body as given.
            }
        }

        return string.Join(' ', body.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries |
                                                     StringSplitOptions.TrimEntries));
    }
}
