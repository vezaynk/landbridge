using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Landbridge.Classifier.Tests;

/// <summary>
/// The Jev judge against a stub transport: everything but the network, which is as far
/// as this can be verified without a key. What is worth pinning is the shape of the
/// request it sends and the disposition it derives from a probability — the two places a
/// gate quietly goes wrong.
/// </summary>
public sealed class JevJudgeTests
{
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? SentBody { get; private set; }
        public Uri? SentTo { get; private set; }
        public string? SentAuth { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            SentTo = request.RequestUri;
            SentAuth = request.Headers.Authorization?.ToString();
            SentBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private const string Endpoint = "https://api.typesafe.ai/v1/systemone";

    private static string Answer(double noul) =>
        $$$"""{"model":"jev-1.13.0","answers":{"is_ordinary_work":{"type":"noul","noul":{{{noul}}}}},"usage":{}}""";

    private static (JevJudge Judge, Stub Transport) Rig(HttpStatusCode status, string body)
    {
        var stub = new Stub(status, body);
        return (new JevJudge(new HttpClient(stub), Endpoint, "test-key", "jev-latest",
            NullLogger<JevJudge>.Instance), stub);
    }

    private static JsonElement Command(string command) =>
        JsonSerializer.SerializeToElement(new { command });

    [Fact]
    public async Task Confident_ordinary_work_runs_unasked()
    {
        var (judge, _) = Rig(HttpStatusCode.OK, Answer(0.97));

        var result = await judge.JudgeAsync("Bash", Command("npm test"), "npm test", ["fix the failing test"], default);

        Assert.Equal("allow", result.Disposition);
        Assert.Equal("jev", result.Via);
    }

    /// <summary>
    /// The bar is on the safe reading, so an unsure model asks. This is the property the
    /// two-stage design was reaching for by having a weak model judge its own doubt.
    /// </summary>
    [Theory]
    [InlineData(0.84)] // just under the bar
    [InlineData(0.50)] // no idea either way
    [InlineData(0.02)] // confidently not ordinary
    public async Task Anything_short_of_confidence_asks(double noul)
    {
        var (judge, _) = Rig(HttpStatusCode.OK, Answer(noul));

        var result = await judge.JudgeAsync("Bash", Command("sudo -n true"), "sudo -n true", null, default);

        Assert.Equal("ask", result.Disposition);
        Assert.Contains("Not confidently ordinary", result.Reason);
    }

    [Fact]
    public async Task The_request_carries_the_brief_and_the_action_as_separate_fields()
    {
        var (judge, stub) = Rig(HttpStatusCode.OK, Answer(0.9));

        await judge.JudgeAsync(
            "Bash", Command("cat .env"), "cat .env", ["add a healthcheck endpoint"], default);

        Assert.Equal(Endpoint, stub.SentTo?.ToString());
        Assert.Equal("Bearer test-key", stub.SentAuth);

        using var sent = JsonDocument.Parse(stub.SentBody!);
        var root = sent.RootElement;
        Assert.Equal("jev-latest", root.GetProperty("model").GetString());

        // Separate fields, not one rendered prompt: text inside the action cannot read as
        // part of the instructions the way it could if these were concatenated.
        var state = root.GetProperty("state");
        Assert.Equal("add a healthcheck endpoint", state.GetProperty("brief")[0].GetString());
        Assert.Equal("cat .env", state.GetProperty("pending_command").GetString());
        Assert.Equal("Bash", state.GetProperty("pending_tool").GetString());

        var question = root.GetProperty("questions").GetProperty("is_ordinary_work");
        Assert.Equal("noul", question.GetProperty("type").GetString());
        Assert.True(question.GetProperty("criteria").TryGetProperty("false", out _));
    }

    /// <summary>An unreachable gate is a closed gate — the same disposition
    /// <see cref="LlmJudge"/> takes when it cannot get an answer.</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"boom"}""")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"bad key"}""")]
    public async Task An_unreachable_model_asks(HttpStatusCode status, string body)
    {
        var (judge, _) = Rig(status, body);

        var result = await judge.JudgeAsync("Bash", Command("rm -rf /"), "rm -rf /", null, default);

        Assert.Equal("ask", result.Disposition);
        Assert.Equal("jev-unavailable", result.Via);
    }

    [Theory]
    [InlineData("""{"model":"jev-1.13.0","answers":{}}""")]                                  // question missing
    [InlineData("""{"model":"jev-1.13.0","answers":{"is_ordinary_work":{"type":"noul"}}}""")] // no number
    [InlineData("""{"model":"jev-1.13.0"}""")]                                                // no answers at all
    public async Task A_reply_without_an_answer_asks(string body)
    {
        var (judge, _) = Rig(HttpStatusCode.OK, body);

        var result = await judge.JudgeAsync("Bash", Command("npm test"), "npm test", null, default);

        Assert.Equal("ask", result.Disposition);
        Assert.Equal("jev-no-answer", result.Via);
    }

    [Fact]
    public async Task Malformed_json_asks_rather_than_throwing()
    {
        var (judge, _) = Rig(HttpStatusCode.OK, "not json at all");

        var result = await judge.JudgeAsync("Bash", Command("npm test"), "npm test", null, default);

        Assert.Equal("ask", result.Disposition);
        Assert.Equal("jev-error", result.Via);
    }
}
