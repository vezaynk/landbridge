using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docket.Relay.Tests;

/// <summary>
/// Both <see cref="IGrantValidator"/> implementations, on the axis that matters:
/// what they do when they are <em>not</em> sure. The relay is the widest hole in
/// the model (§13), and a tunnel is spliced on a validator's word alone, so every
/// doubt — no configuration, a plane that answers non-200, a malformed answer, a
/// plane that never answers — has to come back <c>false</c>. A validator that
/// failed open would still pass every happy-path test.
/// </summary>
public class GrantValidatorTests
{
    private const string Forward = "fwd-1";

    // ── The fail-closed static validator (no control-plane URL configured) ─────

    [Fact]
    public async Task Static_validator_with_no_configuration_refuses_everything()
    {
        // The default registration: no AllowAll, no SharedSecret. This is what runs
        // when an operator stands the relay up without wiring grant validation, so
        // it must refuse rather than splice.
        var validator = Static(new StaticSecretGrantValidatorOptions(), out var log);

        foreach (var grant in new[] { "", "anything", "test-grant", RelayTestKit.GoodGrant })
        {
            Assert.False(await validator.ValidateAsync(grant, Forward, RelayRole.Consumer, default));
            Assert.False(await validator.ValidateAsync(grant, Forward, RelayRole.Producer, default));
        }

        // And it says so once, at construction, rather than silently refusing every
        // tunnel while an operator hunts for the cause.
        Assert.Contains(log.Warnings, w => w.Contains("no grant validator configured"));
    }

    [Fact]
    public async Task Static_validator_accepts_only_an_exact_shared_secret()
    {
        var validator = Static(
            new StaticSecretGrantValidatorOptions { SharedSecret = "s3cret-grant" }, out var log);

        Assert.True(await validator.ValidateAsync("s3cret-grant", Forward, RelayRole.Consumer, default));

        foreach (var wrong in new[]
                 {
                     "",                 // absent
                     "s3cret-gran",      // one byte short — a length mismatch, not a content one
                     "s3cret-grantx",    // one byte long
                     "s3cret-granT",     // last byte differs
                     "S3cret-grant",     // first byte differs
                     "wholly-different",
                 })
        {
            Assert.False(await validator.ValidateAsync(wrong, Forward, RelayRole.Consumer, default));
        }

        // A configured validator is a working one; no warning about missing config.
        Assert.DoesNotContain(log.Warnings, w => w.Contains("no grant validator configured"));
    }

    [Fact]
    public async Task Static_validator_is_indifferent_to_forward_and_role()
    {
        // The static stub authenticates the bearer of a secret and nothing else — it
        // knows no forwards. Pinned so that "it ignores forwardId" stays a deliberate
        // property of the stub rather than something a reader has to infer.
        var validator = Static(new StaticSecretGrantValidatorOptions { SharedSecret = "s" }, out _);

        Assert.True(await validator.ValidateAsync("s", "fwd-1", RelayRole.Consumer, default));
        Assert.True(await validator.ValidateAsync("s", "fwd-2", RelayRole.Producer, default));
        Assert.True(await validator.ValidateAsync("s", "", RelayRole.Producer, default));
    }

    [Fact]
    public async Task Static_validator_allow_all_accepts_anything_and_warns_loudly()
    {
        var validator = Static(new StaticSecretGrantValidatorOptions { AllowAll = true }, out var log);

        Assert.True(await validator.ValidateAsync("", Forward, RelayRole.Consumer, default));
        Assert.True(await validator.ValidateAsync("garbage", Forward, RelayRole.Producer, default));

        // Loudly insecure means loudly: the warning is the only thing standing
        // between this mode and a production deployment that accepts every grant.
        Assert.Contains(log.Warnings, w => w.Contains("every grant is accepted"));
    }

    [Fact]
    public async Task Static_validator_allow_all_overrides_a_configured_secret()
    {
        // Both set is a misconfiguration, and it resolves the dangerous way — so the
        // precedence is worth pinning rather than discovering in production.
        var validator = Static(
            new StaticSecretGrantValidatorOptions { AllowAll = true, SharedSecret = "s3cret" }, out var log);

        Assert.True(await validator.ValidateAsync("not-the-secret", Forward, RelayRole.Consumer, default));
        Assert.Contains(log.Warnings, w => w.Contains("every grant is accepted"));
    }

    // ── The real control-plane validator (§8.3) ───────────────────────────────

    [Fact]
    public async Task Plane_validator_accepts_only_when_the_plane_says_valid()
    {
        var validator = Plane(Json(HttpStatusCode.OK, """{"valid":true}"""), out _, out _);

        Assert.True(await validator.ValidateAsync("g", Forward, RelayRole.Consumer, default));
    }

    [Fact]
    public async Task Plane_validator_refuses_when_the_plane_says_invalid()
    {
        var validator = Plane(Json(HttpStatusCode.OK, """{"valid":false}"""), out _, out _);

        Assert.False(await validator.ValidateAsync("g", Forward, RelayRole.Consumer, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Plane_validator_refuses_on_any_non_success_status(HttpStatusCode status)
    {
        // Including the statuses that are most tempting to treat as "the plane is
        // having a moment": a 503 is still not permission to splice a tunnel. Note
        // the body says valid — the status alone decides, so a plane that returned
        // an error page containing arbitrary JSON cannot authorize anything.
        var validator = Plane(Json(status, """{"valid":true}"""), out var log, out _);

        Assert.False(await validator.ValidateAsync("g", Forward, RelayRole.Producer, default));
        Assert.Contains(log.Warnings, w => w.Contains("refusing"));
    }

    [Theory]
    [InlineData("")]                          // empty body
    [InlineData("not json at all")]           // unparseable
    [InlineData("null")]                      // valid JSON, deserializes to null
    [InlineData("""{"unexpected":"shape"}""")] // no valid property → default false
    [InlineData("""[{"valid":true}]""")]      // right data, wrong shape
    [InlineData("""{"valid":"true"}""")]      // string, not bool
    public async Task Plane_validator_refuses_a_malformed_answer(string body)
    {
        // A 200 whose body the relay cannot read as a verdict is doubt, not consent.
        var validator = Plane(Json(HttpStatusCode.OK, body), out _, out _);

        Assert.False(await validator.ValidateAsync("g", Forward, RelayRole.Consumer, default));
    }

    [Fact]
    public async Task Plane_validator_refuses_when_the_plane_is_unreachable()
    {
        var validator = Plane(
            _ => throw new HttpRequestException("connection refused"), out var log, out _);

        Assert.False(await validator.ValidateAsync("g", Forward, RelayRole.Consumer, default));
        Assert.Contains(log.Warnings, w => w.Contains("refusing"));
    }

    [Fact]
    public async Task Plane_validator_refuses_when_the_plane_does_not_answer_in_time()
    {
        // The timeout is the tunnel-open path's patience, and it has to fail closed:
        // a plane that hangs must not hold a tunnel open pending an answer.
        var validator = Plane(
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("the delay must not complete");
            },
            out var log,
            out _,
            timeout: TimeSpan.FromMilliseconds(100));

        Assert.False(await validator.ValidateAsync("g", Forward, RelayRole.Consumer, default));
        Assert.Contains(log.Warnings, w => w.Contains("refusing"));
    }

    [Fact]
    public async Task Plane_validator_refuses_when_its_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var validator = Plane(Json(HttpStatusCode.OK, """{"valid":true}"""), out _, out _);

        // Even a plane that would have said yes: a cancelled validation is not a
        // completed one, and the catch-all is what keeps that from throwing into the
        // tunnel-open path.
        Assert.False(await validator.ValidateAsync("g", Forward, RelayRole.Consumer, cts.Token));
    }

    [Fact]
    public async Task Plane_validator_presents_the_bearer_and_the_grant_it_was_given()
    {
        var validator = Plane(
            Json(HttpStatusCode.OK, """{"valid":true}"""), out var log, out var sent, bearer: "plane-bearer");

        Assert.True(await validator.ValidateAsync("dkt_g_abc", "fwd-77", RelayRole.Consumer, default));

        var request = Assert.Single(sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://plane.example/relay/validate", request.Url);
        Assert.Equal("Bearer plane-bearer", request.Authorization);
        // The plane decides per {grant, forwardId, role}, so all three have to reach it.
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("dkt_g_abc", body.RootElement.GetProperty("grant").GetString());
        Assert.Equal("fwd-77", body.RootElement.GetProperty("forwardId").GetString());
        Assert.Equal("consumer", body.RootElement.GetProperty("role").GetString());

        Assert.DoesNotContain(log.Warnings, w => w.Contains("no bearer configured"));
    }

    [Theory]
    [InlineData(RelayRole.Consumer, "consumer")]
    [InlineData(RelayRole.Producer, "producer")]
    public async Task Plane_validator_sends_the_wire_spelling_of_each_role(RelayRole role, string expected)
    {
        // The two ends are asymmetric — a grant valid for the producer is not
        // automatically valid for the consumer — so the role has to arrive in the
        // spelling the plane's endpoint matches on.
        var validator = Plane(Json(HttpStatusCode.OK, """{"valid":true}"""), out _, out var sent);

        await validator.ValidateAsync("g", Forward, role, default);

        using var body = JsonDocument.Parse(Assert.Single(sent).Body);
        Assert.Equal(expected, body.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Plane_validator_warns_when_no_bearer_is_configured_and_sends_none()
    {
        // The plane refuses an unauthenticated validation, so this configuration
        // refuses every tunnel. The warning is what tells the operator why.
        var validator = Plane(
            Json(HttpStatusCode.Unauthorized, ""), out var log, out var sent, bearer: null);

        Assert.False(await validator.ValidateAsync("g", Forward, RelayRole.Consumer, default));

        Assert.Contains(log.Warnings, w => w.Contains("no bearer configured"));
        Assert.Null(Assert.Single(sent).Authorization);
    }

    [Theory]
    [InlineData("http://plane.example", "http://plane.example/relay/validate")]
    [InlineData("http://plane.example/", "http://plane.example/relay/validate")]
    [InlineData("http://plane.example///", "http://plane.example/relay/validate")]
    public async Task Plane_validator_builds_one_validate_url_regardless_of_trailing_slashes(
        string configured, string expected)
    {
        var validator = Plane(
            Json(HttpStatusCode.OK, """{"valid":true}"""), out _, out var sent, url: configured);

        await validator.ValidateAsync("g", Forward, RelayRole.Consumer, default);

        Assert.Equal(expected, Assert.Single(sent).Url);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static StaticSecretGrantValidator Static(
        StaticSecretGrantValidatorOptions options, out CollectingLogger log)
    {
        var logger = new CollectingLogger();
        log = logger;
        return new StaticSecretGrantValidator(Options.Create(options), logger.For<StaticSecretGrantValidator>());
    }

    private static ControlPlaneGrantValidator Plane(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        out CollectingLogger log,
        out List<SentRequest> sent,
        string url = "http://plane.example",
        string? bearer = "plane-bearer",
        TimeSpan? timeout = null)
    {
        var logger = new CollectingLogger();
        log = logger;
        var captured = new List<SentRequest>();
        sent = captured;
        var options = Options.Create(new ControlPlaneGrantValidatorOptions
        {
            Url = url,
            Bearer = bearer,
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        });
        return new ControlPlaneGrantValidator(
            new StubHttpClientFactory(captured, respond),
            options,
            logger.For<ControlPlaneGrantValidator>());
    }

    private static Func<CancellationToken, Task<HttpResponseMessage>> Json(HttpStatusCode status, string body) =>
        _ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    private sealed record SentRequest(HttpMethod Method, string Url, string? Authorization, string Body);

    private sealed class StubHttpClientFactory(
        List<SentRequest> sent, Func<CancellationToken, Task<HttpResponseMessage>> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            // The validator asks for its own named client; a factory that ignored the
            // name would hide a wiring mistake, so assert the contract here.
            Assert.Equal(ControlPlaneGrantValidator.HttpClientName, name);
            return new HttpClient(new StubHandler(sent, respond));
        }
    }

    private sealed class StubHandler(
        List<SentRequest> sent, Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            sent.Add(new SentRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return await respond(ct);
        }
    }

    /// <summary>Captures warning text so the "and it says why" half of fail-closed is assertable.</summary>
    private sealed class CollectingLogger
    {
        public List<string> Warnings { get; } = [];

        public ILogger<T> For<T>() => new Typed<T>(this);

        private sealed class Typed<T>(CollectingLogger owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    owner.Warnings.Add(formatter(state, exception));
            }
        }
    }
}
