using System.Net;
using System.Text;
using Landbridge.ControlPlane.Auth;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// The CIMD fetcher's refusal surface (<c>draft-ietf-oauth-client-id-metadata-document-00</c>
/// §3, §4.1, §6.5–6.6). This is the one place the authorization server fetches an
/// <em>attacker-supplied</em> URL, so the interesting behaviour is entirely in what
/// it declines to do: the address fence that keeps a <c>client_id</c> from steering
/// the fetcher at the internal network, and the document checks that keep a
/// well-formed response from impersonating another client.
///
/// <para>Every case here is hermetic. The address-fence cases use IP literals, which
/// the fence rejects <em>before</em> any socket is opened, so they neither touch the
/// network nor depend on DNS; the document cases talk to a loopback listener.</para>
/// </summary>
public class CimdClientTests
{
    /// <summary>
    /// Addresses a <c>client_id</c> must never be able to steer the fetcher at
    /// (draft §6.5). Each is the canonical member of a range the fence blocks —
    /// <c>169.254.169.254</c> most of all, the cloud instance-metadata endpoint that
    /// makes server-side request forgery worth attempting in the first place.
    /// </summary>
    public static TheoryData<string, string> BlockedAddresses() => new()
    {
        { "10.0.0.1", "RFC 1918 10/8" },
        { "10.255.255.254", "RFC 1918 10/8, top of range" },
        { "172.16.0.1", "RFC 1918 172.16/12, bottom of range" },
        { "172.31.255.254", "RFC 1918 172.16/12, top of range" },
        { "172.20.10.5", "RFC 1918 172.16/12, middle" },
        { "192.168.0.1", "RFC 1918 192.168/16" },
        { "192.168.255.254", "RFC 1918 192.168/16, top of range" },
        { "169.254.169.254", "link-local — the cloud metadata endpoint" },
        { "169.254.0.1", "link-local 169.254/16" },
        { "100.64.0.1", "CGNAT 100.64/10, bottom of range" },
        { "100.127.255.254", "CGNAT 100.64/10, top of range" },
        { "127.0.0.1", "loopback" },
        { "127.1.2.3", "loopback, not just .0.1" },
        { "0.0.0.0", "unspecified / wildcard" },
        { "0.1.2.3", "0/8" },
        { "224.0.0.1", "multicast" },
        { "239.255.255.255", "multicast, top of range" },
        { "255.255.255.255", "broadcast" },
        { "240.0.0.1", "reserved, above the multicast block" },
        { "[::1]", "IPv6 loopback" },
        { "[::]", "IPv6 unspecified" },
        { "[fc00::1]", "IPv6 unique-local fc00::/7" },
        { "[fd12:3456:789a::1]", "IPv6 unique-local, fd-prefixed" },
        { "[fe80::1]", "IPv6 link-local" },
        { "[ff02::1]", "IPv6 multicast" },
        { "[::ffff:10.0.0.1]", "IPv4-mapped IPv6 smuggling a private v4 address" },
        { "[::ffff:127.0.0.1]", "IPv4-mapped IPv6 smuggling loopback" },
        { "[::ffff:169.254.169.254]", "IPv4-mapped IPv6 smuggling the metadata endpoint" },
    };

    [Theory]
    [MemberData(nameof(BlockedAddresses))]
    public async Task The_address_fence_refuses_a_client_id_pointed_at_a_non_public_address(
        string host, string why)
    {
        // The URL itself is well-formed — absolute, https, with a path — so the shape
        // gate passes it and the connect-time address check is what has to refuse.
        var client = new CimdClient(allowInsecure: false);

        var result = await client.FetchAsync($"https://{host}/client.json");

        var failed = Assert.IsType<CimdResult.Failed>(result);
        // Asserting the guard's own words, not merely that it failed: a fence that
        // had let this address through would still fail (nothing is listening there),
        // and the two outcomes must not be confusable.
        Assert.True(
            failed.Reason.Contains("SSRF guard", StringComparison.Ordinal),
            $"{host} ({why}) must be refused by the address fence, but was: {failed.Reason}");
    }

    [Fact]
    public async Task The_insecure_flag_is_what_lets_a_private_address_past_the_fence()
    {
        // The discriminating pair, and the reason the theory above is a statement
        // about the fence rather than about loopback being unreachable here: one URL,
        // two clients. Fenced, it never opens a socket; relaxed, it connects for real
        // and fails on its own terms because nothing is listening on that port.
        var url = $"https://127.0.0.1:{CimdServer.ClosedPort()}/client.json";

        var fenced = Assert.IsType<CimdResult.Failed>(
            await new CimdClient(allowInsecure: false).FetchAsync(url));
        var relaxed = Assert.IsType<CimdResult.Failed>(
            await new CimdClient(allowInsecure: true).FetchAsync(url));

        Assert.Contains("SSRF guard", fenced.Reason);
        Assert.DoesNotContain("SSRF guard", relaxed.Reason);
        Assert.Contains("fetch failed", relaxed.Reason);
    }

    // ── Shape of the client_id URL (draft §3) ─────────────────────────────────

    [Theory]
    // Not a URL at all, so there is nothing to fetch.
    [InlineData("not-a-url", "not an absolute URL")]
    // A leading-slash string parses as an absolute file: URI on Unix, so it is the
    // scheme gate that turns it away rather than the absolute-URL gate.
    [InlineData("/relative/client.json", "must use https")]
    [InlineData("", "not an absolute URL")]
    // Wrong scheme: https is required unless the dev/test relaxation is on.
    [InlineData("http://example.com/client.json", "must use https")]
    [InlineData("ftp://example.com/client.json", "must use https")]
    [InlineData("file:///etc/passwd", "must use https")]
    // Userinfo would let a client_id smuggle credentials into the fetch.
    [InlineData("https://user:pass@example.com/client.json", "must not contain userinfo")]
    [InlineData("https://user@example.com/client.json", "must not contain userinfo")]
    // A fragment is not sent on the wire, so a client_id carrying one could never
    // equal the document's own client_id by string comparison (§4.1).
    [InlineData("https://example.com/client.json#frag", "must not contain a fragment")]
    // No path component: the draft requires one, which keeps a bare origin from
    // being a client identity.
    [InlineData("https://example.com", "must contain a path component")]
    [InlineData("https://example.com/", "must contain a path component")]
    public async Task A_malformed_client_id_is_refused_without_a_fetch(string clientId, string expected)
    {
        // Each of these is refused on shape alone, before any DNS or socket work —
        // note that example.com is never contacted even though it resolves.
        var client = new CimdClient(allowInsecure: false);

        var result = await client.FetchAsync(clientId);

        var failed = Assert.IsType<CimdResult.Failed>(result);
        Assert.Contains(expected, failed.Reason);
    }

    [Fact]
    public async Task The_insecure_flag_relaxes_the_scheme_but_not_the_rest_of_the_shape()
    {
        // The dev/test relaxation is scoped: it permits http and private addresses,
        // and leaves every other §3 requirement in force. A flag that had quietly
        // relaxed all of them would be a much bigger hole than the one documented.
        var client = new CimdClient(allowInsecure: true);

        Assert.Contains(
            "must not contain userinfo",
            Assert.IsType<CimdResult.Failed>(await client.FetchAsync("http://u:p@127.0.0.1/c.json")).Reason);
        Assert.Contains(
            "must not contain a fragment",
            Assert.IsType<CimdResult.Failed>(await client.FetchAsync("http://127.0.0.1/c.json#f")).Reason);
        Assert.Contains(
            "must contain a path component",
            Assert.IsType<CimdResult.Failed>(await client.FetchAsync("http://127.0.0.1")).Reason);
        Assert.Contains(
            "not an absolute URL",
            Assert.IsType<CimdResult.Failed>(await client.FetchAsync("nonsense")).Reason);
    }

    // ── The document itself (draft §4.1) ──────────────────────────────────────

    [Fact]
    public async Task A_valid_document_is_accepted_with_the_fields_landbridge_reads()
    {
        using var server = CimdServer.Serving();

        var ok = Assert.IsType<CimdResult.Ok>(await Fetch(server));

        Assert.Equal(server.ClientId, ok.Document.ClientId);
        Assert.Equal("Test Client", ok.Document.ClientName);
        Assert.Equal(["http://127.0.0.1/callback"], ok.Document.RedirectUris);
        Assert.Equal("native", ok.Document.ApplicationType);
    }

    [Fact]
    public async Task Unknown_members_are_ignored_rather_than_refused()
    {
        // §4: only the fields Landbridge uses are modelled, and a document carrying more
        // is still a valid document — the registry is not ours to police.
        using var server = CimdServer.Raw(clientId => $$"""
            { "client_id": "{{clientId}}", "client_name": "Test Client",
              "redirect_uris": ["http://127.0.0.1/callback"],
              "token_endpoint_auth_method": "none", "some_future_field": {"nested": [1,2,3]} }
            """);

        var ok = Assert.IsType<CimdResult.Ok>(await Fetch(server));
        Assert.Equal("Test Client", ok.Document.ClientName);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task A_non_success_fetch_is_refused_and_names_the_status(
        HttpStatusCode status, string expected)
    {
        using var server = CimdServer.Status(status);

        var failed = Assert.IsType<CimdResult.Failed>(await Fetch(server));

        Assert.Contains(expected, failed.Reason);
        Assert.Contains("fetch returned", failed.Reason);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_it_cannot_bounce_the_fetcher_inward()
    {
        // §6.5: AllowAutoRedirect is off, so a public client_id URL cannot 3xx the
        // fetcher at an internal address — the 302 is simply a non-success status.
        using var server = CimdServer.Redirecting("http://169.254.169.254/latest/meta-data/");

        var failed = Assert.IsType<CimdResult.Failed>(await Fetch(server));

        Assert.Contains("fetch returned", failed.Reason);
        Assert.Contains("302", failed.Reason);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"client_id\": ")]
    [InlineData("<html>nope</html>")]
    public async Task A_body_that_is_not_json_is_refused(string body)
    {
        using var server = CimdServer.Raw(_ => body);

        Assert.Contains(
            "not valid JSON", Assert.IsType<CimdResult.Failed>(await Fetch(server)).Reason);
    }

    [Fact]
    public async Task A_json_null_body_is_refused_as_empty()
    {
        // Valid JSON that deserializes to no document at all — distinct from
        // unparseable, and distinct from a document missing fields.
        using var server = CimdServer.Raw(_ => "null");

        Assert.Contains(
            "metadata is empty", Assert.IsType<CimdResult.Failed>(await Fetch(server)).Reason);
    }

    [Theory]
    // Each omits exactly one of the three members §4.1 requires.
    [InlineData("""{ "client_name": "C", "redirect_uris": ["http://127.0.0.1/cb"] }""")]
    [InlineData("""{ "client_id": "@@", "redirect_uris": ["http://127.0.0.1/cb"] }""")]
    [InlineData("""{ "client_id": "@@", "client_name": "C" }""")]
    // Present but empty is the same as absent — an empty redirect_uris list would
    // leave nowhere to send the authorization code.
    [InlineData("""{ "client_id": "@@", "client_name": "C", "redirect_uris": [] }""")]
    [InlineData("""{ "client_id": "", "client_name": "C", "redirect_uris": ["http://127.0.0.1/cb"] }""")]
    [InlineData("""{ "client_id": "@@", "client_name": "", "redirect_uris": ["http://127.0.0.1/cb"] }""")]
    [InlineData("{ }")]
    public async Task A_document_missing_a_required_member_is_refused(string template)
    {
        using var server = CimdServer.Raw(clientId => template.Replace("@@", clientId));

        Assert.Contains(
            "missing client_id, client_name, or redirect_uris",
            Assert.IsType<CimdResult.Failed>(await Fetch(server)).Reason);
    }

    [Theory]
    [InlineData("https://elsewhere.example/client.json")]
    [InlineData("http://127.0.0.1:1/client.json")]
    // §4.1 says simple string comparison, so a trailing slash or a case difference
    // is a mismatch even though the URLs would resolve the same way.
    [InlineData("@@/")]
    [InlineData("@@?")]
    public async Task A_document_whose_client_id_does_not_match_the_url_is_refused(string embedded)
    {
        // This is the check that stops one client from claiming another's identity:
        // the document is well-formed and complete, and is still refused because the
        // id inside it is not the URL it was served from.
        using var server = CimdServer.Raw(clientId => $$"""
            { "client_id": "{{embedded.Replace("@@", clientId)}}", "client_name": "Impostor",
              "redirect_uris": ["http://127.0.0.1/callback"] }
            """);

        Assert.Contains(
            "does not match the client_id URL",
            Assert.IsType<CimdResult.Failed>(await Fetch(server)).Reason);
    }

    // ── The body cap (draft §6.6) ─────────────────────────────────────────────

    [Theory]
    [InlineData(5 * 1024 + 1, "one byte over")]
    [InlineData(6 * 1024, "6 KB")]
    [InlineData(64 * 1024, "64 KB")]
    [InlineData(1024 * 1024, "1 MB")]
    public async Task A_body_over_the_size_cap_is_refused_however_far_over_it_goes(
        int bytes, string how)
    {
        // The endpoint fetches an attacker-supplied URL, so the response size is the
        // attacker's choice: without a cap, exposure is whatever can be streamed
        // inside the 5s timeout. Each of these documents is otherwise perfectly valid
        // and complete — only its length is the problem, so this is a statement about
        // the cap and not about a malformed body being rejected on other grounds.
        using var server = CimdServer.Raw(clientId => Padded(clientId, bytes));

        var failed = Assert.IsType<CimdResult.Failed>(await Fetch(server));

        Assert.True(
            failed.Reason.Contains("exceeds the 5 KB cap", StringComparison.Ordinal),
            $"a {how} body must be refused by the size cap, but was: {failed.Reason}");
    }

    [Fact]
    public async Task A_body_exactly_at_the_size_cap_is_still_accepted()
    {
        // The other half of the pair: a cap that refused legal documents would pass
        // the theory above and break every real client. 5120 bytes is the largest
        // body the draft's recommended maximum permits, so it has to be read whole.
        using var server = CimdServer.Raw(clientId => Padded(clientId, 5 * 1024));

        var ok = Assert.IsType<CimdResult.Ok>(await Fetch(server));

        Assert.Equal("Test Client", ok.Document.ClientName);
    }

    /// <summary>
    /// A valid document padded out with an ignored member to exactly
    /// <paramref name="bytes"/> bytes, so a test can sit on the cap or step over it
    /// by a single byte. ASCII throughout, so the byte count is the character count.
    /// </summary>
    private static string Padded(string clientId, int bytes)
    {
        var document = $$"""
            {"client_id":"{{clientId}}","client_name":"Test Client","redirect_uris":["http://127.0.0.1/callback"],"padding":"@@"}
            """;
        return document.Replace("@@", new string('x', bytes - document.Length + 2));
    }

    private static Task<CimdResult> Fetch(CimdServer server) =>
        // Insecure is on because the test document is served from loopback http; the
        // address fence itself is what the theory at the top of the file covers.
        new CimdClient(allowInsecure: true).FetchAsync(server.ClientId);

    /// <summary>
    /// A loopback HTTP listener serving one document at <c>/client.json</c>. Uses
    /// <see cref="HttpListener"/> rather than a web host so the suite needs no
    /// ASP.NET framework reference for what is a two-line static file server.
    /// </summary>
    private sealed class CimdServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stopping = new();

        public string ClientId { get; }

        private CimdServer(int port, Func<string, (HttpStatusCode Status, string? Body, string? Location)> respond)
        {
            ClientId = $"http://127.0.0.1:{port}/client.json";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = ServeAsync(respond);
        }

        public static CimdServer Serving() =>
            Raw(clientId => $$"""
                { "client_id": "{{clientId}}", "client_name": "Test Client",
                  "client_uri": "http://127.0.0.1/", "application_type": "native",
                  "redirect_uris": ["http://127.0.0.1/callback"] }
                """);

        public static CimdServer Raw(Func<string, string> body) =>
            new(FreePort(), clientId => (HttpStatusCode.OK, body(clientId), null));

        public static CimdServer Status(HttpStatusCode status) =>
            new(FreePort(), _ => (status, null, null));

        public static CimdServer Redirecting(string location) =>
            new(FreePort(), _ => (HttpStatusCode.Found, null, location));

        private async Task ServeAsync(Func<string, (HttpStatusCode Status, string? Body, string? Location)> respond)
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_stopping.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                try
                {
                    var (status, body, location) = respond(ClientId);
                    context.Response.StatusCode = (int)status;
                    if (location is not null)
                        context.Response.Headers["Location"] = location;
                    if (body is not null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    context.Response.Close();
                }
                catch (Exception e) when (e is IOException or HttpListenerException)
                {
                    // The over-cap cases stop reading one byte past the cap and hang up,
                    // so writing out the rest of a large body legitimately fails here.
                }
            }
        }

        /// <summary>A loopback port with nothing listening on it.</summary>
        public static int ClosedPort() => FreePort();

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Close();
            _stopping.Dispose();
        }
    }
}
