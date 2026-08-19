using System.Net;
using System.Net.Http.Json;
using Landbridge.Core;
using Landbridge.Mcp;

namespace Landbridge.Mcp.Tests;

public sealed class PermissionClassifierTests
{
    private static readonly SessionId Session = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    [Fact]
    public async Task Client_posts_camel_case_tool_and_input()
    {
        string? posted = null;
        var http = ClientFor((req, _) =>
        {
            posted = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(new { disposition = "allow", via = "readonly-shell" });
        });
        await http.ClassifyAsync(Session, "Bash", """{"command":"git --version"}""", [], CancellationToken.None);
        Assert.NotNull(posted);
        Assert.Contains("\"tool\":\"Bash\"", posted, StringComparison.Ordinal);
        Assert.Contains("\"input\":", posted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Tool\":", posted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"messages\":", posted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_posts_lead_messages()
    {
        string? posted = null;
        var http = ClientFor((req, _) =>
        {
            posted = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(new { disposition = "ask", via = "classifier-fast" });
        });
        await http.ClassifyAsync(
            Session, "Bash", """{"command":"pytest"}""",
            ["add a failing test", "use pytest"], CancellationToken.None);
        Assert.NotNull(posted);
        Assert.Contains("\"messages\":[\"add a failing test\",\"use pytest\"]", posted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_maps_allow_to_auto_allow()
    {
        var http = ClientFor((_, _) => Json(new { disposition = "allow", via = "readonly-shell" }));
        var got = await http.ClassifyAsync(Session, "Bash", """{"command":"git status"}""", [], CancellationToken.None);
        Assert.Equal(PermissionDisposition.AutoAllow, got);
    }

    [Fact]
    public async Task Client_maps_ask_and_errors_to_ask()
    {
        var ask = ClientFor((_, _) => Json(new { disposition = "ask", via = "not-readonly" }));
        Assert.Equal(
            PermissionDisposition.Ask,
            await ask.ClassifyAsync(Session, "Bash", """{"command":"rm -rf /"}""", [], CancellationToken.None));

        var fail = ClientFor((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Equal(
            PermissionDisposition.Ask,
            await fail.ClassifyAsync(Session, "Bash", "{}", [], CancellationToken.None));
    }

    [Fact]
    public async Task Client_never_denies()
    {
        var http = ClientFor((_, _) => Json(new { disposition = "deny", via = "nope" }));
        Assert.Equal(
            PermissionDisposition.Ask,
            await http.ClassifyAsync(Session, "Bash", """{"command":"ls"}""", [], CancellationToken.None));
    }

    [Fact]
    public async Task Null_classifier_always_asks()
    {
        Assert.Equal(
            PermissionDisposition.Ask,
            await NullPermissionClassifier.Instance.ClassifyAsync(Session, "Bash", "{}", [], CancellationToken.None));
    }

    [Fact]
    public async Task Relay_allows_without_opening_a_wait_when_classifier_allows()
    {
        var classified = new FixedClassifier(PermissionDisposition.AutoAllow);
        var result = await PermissionRelay.OpenAndAwaitAsync(
            store: null!,
            caller: new WorkerCaller(TeamId.New(), Session, WorkerInstanceId.New()),
            tool: "Bash",
            proposedInput: """{"command":"git status"}""",
            pollInterval: TimeSpan.FromMilliseconds(1),
            clock: TimeProvider.System,
            ct: CancellationToken.None,
            classifier: classified);
        Assert.True(result.Allow);
        Assert.True(classified.Called);
    }

    private static PermissionClassifierClient ClientFor(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        => new(new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri("http://classifier.test/"),
        });

    private static HttpResponseMessage Json(object body)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request, cancellationToken));
    }

    private sealed class FixedClassifier(PermissionDisposition disposition) : IPermissionClassifier
    {
        public bool Called { get; private set; }

        public Task<PermissionDisposition> ClassifyAsync(
            SessionId session, string tool, string proposedInput,
            IReadOnlyList<string> leadMessages, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(disposition);
        }
    }
}
