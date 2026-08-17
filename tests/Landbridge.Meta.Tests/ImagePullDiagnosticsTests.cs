using Landbridge.Meta.Substrate;

namespace Landbridge.Meta.Tests;

/// <summary>
/// What an operator reads when provisioning stalls on the first step. A pull failure is
/// the most likely way a real deployment fails — the tag is not published yet, or the
/// package is private and meta pulls anonymously — and the raw Docker error is actively
/// misleading: a denied GHCR pull arrives as an <c>InternalServerError</c> whose body is
/// <c>{"message":"error from registry: denied\ndenied"}</c>, naming neither the image nor
/// the cause. These pin the translation. The bodies below are verbatim from a real
/// daemon (see the Docker-gated companion in <see cref="DockerE2ETests"/>).
/// </summary>
public class ImagePullDiagnosticsTests
{
    private const string GhcrDenied = """{"message":"error from registry: denied\ndenied"}""";
    private const string HubMissingTag =
        """{"message":"failed to resolve reference \"docker.io/library/nginx:nope\": docker.io/library/nginx:nope: not found"}""";

    [Fact]
    public void A_denied_pull_names_the_image_and_both_possible_causes()
    {
        var ex = new ImagePullException("ghcr.io/vezaynk/landbridge-mcp:v0.4.0", 500, GhcrDenied);

        // The image reference is the one thing the raw error never carries.
        Assert.Contains("ghcr.io/vezaynk/landbridge-mcp:v0.4.0", ex.Message);
        // A registry answers "denied" for private-without-credentials AND for absent, so
        // the message must offer both rather than assert one.
        Assert.Contains("not published", ex.Message);
        Assert.Contains("private", ex.Message);
        // The actual constraint: meta sends no credentials, so "log in on the host" is not
        // the fix and the message must not imply it is.
        Assert.Contains("anonymously", ex.Message);
        Assert.Contains("docs/META.md", ex.Message);
        // And the registry's own words survive, unwrapped from the JSON envelope.
        Assert.Contains("denied", ex.RegistryDetail);
        Assert.DoesNotContain("{", ex.Message);
    }

    [Fact]
    public void A_missing_tag_points_at_the_publish_workflow()
    {
        var ex = new ImagePullException("docker.io/library/nginx:nope", 404, HubMissingTag);

        Assert.Contains("docker.io/library/nginx:nope", ex.Message);
        Assert.Contains("no such tag", ex.Message);
        // Where tags come from — the operator's next action.
        Assert.Contains("publish-images", ex.Message);
        Assert.Contains("sha-", ex.Message);
    }

    [Fact]
    public void A_404_is_treated_as_a_missing_tag_even_without_a_helpful_body()
    {
        var ex = new ImagePullException("ghcr.io/o/r:t", 404, "");

        Assert.Contains("no such tag", ex.Message);
        Assert.Equal("no detail reported", ex.RegistryDetail);
    }

    [Fact]
    public void An_unrecognized_failure_still_names_the_image_and_the_detail()
    {
        var ex = new ImagePullException("ghcr.io/o/r:t", 500, """{"message":"dial tcp: i/o timeout"}""");

        Assert.Contains("ghcr.io/o/r:t", ex.Message);
        Assert.Contains("dial tcp: i/o timeout", ex.Message);
        // No guessing at a cause it cannot know.
        Assert.DoesNotContain("private", ex.Message);
        Assert.DoesNotContain("no such tag", ex.Message);
    }

    [Fact]
    public void The_detail_is_one_line_because_a_step_log_is_one_line()
    {
        // The step error is persisted and rendered inline; an embedded newline would break
        // the detail page's layout and truncate the useful half.
        var ex = new ImagePullException("i:t", 500, GhcrDenied);

        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.Equal("error from registry: denied denied", ex.RegistryDetail);
    }

    [Fact]
    public void A_non_json_body_is_used_as_given()
    {
        var ex = new ImagePullException("i:t", 500, "plain text failure");

        Assert.Equal("plain text failure", ex.RegistryDetail);
        Assert.Contains("plain text failure", ex.Message);
    }

    [Fact]
    public void The_original_docker_exception_is_preserved_for_the_log()
    {
        var inner = new InvalidOperationException("raw docker detail");
        var ex = new ImagePullException("i:t", 500, GhcrDenied, inner);

        // RunStepAsync persists ex.Message but logs the exception; the cause must survive
        // for the log to be worth anything.
        Assert.Same(inner, ex.InnerException);
        Assert.Equal("i:t", ex.Image);
    }
}
