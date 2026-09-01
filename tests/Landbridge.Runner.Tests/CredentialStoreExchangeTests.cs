using System.Net;
using System.Text;
using System.Text.Json;

namespace Landbridge.Runner.Tests;

/// <summary>
/// The two HTTP exchanges behind landbridged's credentials (spec §5 Bootstrap, §13)
/// and the one read that has to survive a bad file. Their failure contracts are
/// deliberately different, and the difference is the point: a refused enrollment
/// is a fatal, operator-facing error, while a refused refresh is just a failed
/// tick the refresh loop retries. Getting those the wrong way round would either
/// crash a running daemon or silently swallow a bad enrollment token.
/// </summary>
public class CredentialStoreExchangeTests
{
    private static readonly DateTimeOffset AccessExp = DateTimeOffset.Parse("2026-07-29T13:00:00+00:00");
    private static readonly DateTimeOffset RefreshExp = DateTimeOffset.Parse("2026-10-27T12:00:00+00:00");

    private const string Plane = "https://plane.example.com";

    private static EnrollRequest Request() =>
        new("lbr_e_token", "machine-a", "darwin");

    // ── Enrollment (§5) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Enrollment_maps_the_plane_response_onto_the_credential_file()
    {
        var body = JsonSerializer.Serialize(new
        {
            machineId = "11111111-1111-1111-1111-111111111111",
            accessToken = "lbr_m_aaa",
            accessExpiresAt = AccessExp,
            refreshToken = "lbr_r_bbb",
            refreshExpiresAt = RefreshExp,
        });
        using var http = Responding(HttpStatusCode.OK, body, out var sent);

        var creds = await CredentialStore.EnrollAsync(http, Plane, Request(), default);

        Assert.Equal("11111111-1111-1111-1111-111111111111", creds.MachineId);
        Assert.Equal("lbr_m_aaa", creds.AccessToken);
        Assert.Equal(AccessExp, creds.AccessExpiresAt);
        Assert.Equal("lbr_r_bbb", creds.RefreshToken);
        Assert.Equal(RefreshExp, creds.RefreshExpiresAt);
        // The plane never echoes its own URL, so the record is stamped with the base
        // we enrolled against — that is what later refreshes and the derived runner
        // WebSocket URL are built from, with no further argv.
        Assert.Equal(Plane, creds.ControlUrl);
    }

    [Fact]
    public async Task Enrollment_posts_the_token_to_the_planes_enroll_endpoint()
    {
        using var http = Responding(HttpStatusCode.OK, EnrollBody(), out var sent);

        await CredentialStore.EnrollAsync(http, Plane, Request(), default);

        var request = Assert.Single(sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://plane.example.com/enroll", request.Url);
        using var posted = JsonDocument.Parse(request.Body);
        Assert.Equal("lbr_e_token", posted.RootElement.GetProperty("enrollmentToken").GetString());
        Assert.Equal("machine-a", posted.RootElement.GetProperty("name").GetString());
        Assert.Equal("darwin", posted.RootElement.GetProperty("os").GetString());
        Assert.False(posted.RootElement.TryGetProperty("purpose", out _));
        Assert.False(posted.RootElement.TryGetProperty("permissionLevel", out _));
    }

    [Theory]
    [InlineData("https://plane.example.com")]
    [InlineData("https://plane.example.com/")]
    [InlineData("https://plane.example.com///")]
    public async Task Enrollment_builds_one_enroll_url_regardless_of_trailing_slashes(string configured)
    {
        // The base comes from an operator's argv, so it arrives however they typed it.
        using var http = Responding(HttpStatusCode.OK, EnrollBody(), out var sent);

        await CredentialStore.EnrollAsync(http, configured, Request(), default);

        Assert.Equal("https://plane.example.com/enroll", Assert.Single(sent).Url);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_refused_enrollment_throws_with_an_operator_facing_message(HttpStatusCode status)
    {
        // Enrollment is a one-shot operator command, so a refusal has to surface as a
        // non-zero exit with a readable cause rather than a half-written credential
        // file. Note the body is a valid response — the status alone decides.
        using var http = Responding(status, EnrollBody(), out _);

        var ex = await Assert.ThrowsAsync<EnrollmentException>(
            () => CredentialStore.EnrollAsync(http, Plane, Request(), default));

        Assert.Contains("enrollment refused", ex.Message);
        Assert.Contains(((int)status).ToString(), ex.Message);
        // The three things an operator can actually act on.
        Assert.Contains("used", ex.Message);
        Assert.Contains("expired", ex.Message);
        Assert.Contains("invalid", ex.Message);
    }

    [Fact]
    public async Task An_enrollment_that_returns_no_body_throws_rather_than_inventing_a_record()
    {
        // A 200 with a null body would otherwise deserialize to null and produce a
        // credential file full of empty strings — a machine that believes it enrolled
        // and can never authenticate.
        using var http = Responding(HttpStatusCode.OK, "null", out _);

        var ex = await Assert.ThrowsAsync<EnrollmentException>(
            () => CredentialStore.EnrollAsync(http, Plane, Request(), default));

        Assert.Contains("empty body", ex.Message);
    }

    // ── Refresh (§13) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_returns_the_fresh_access_token()
    {
        var body = JsonSerializer.Serialize(new { accessToken = "lbr_m_ccc", accessExpiresAt = AccessExp });
        using var http = Responding(HttpStatusCode.OK, body, out var sent);

        var refreshed = await CredentialStore.RefreshAsync(http, Plane, "lbr_r_bbb", default);

        Assert.NotNull(refreshed);
        Assert.Equal("lbr_m_ccc", refreshed!.AccessToken);
        Assert.Equal(AccessExp, refreshed.AccessExpiresAt);

        var request = Assert.Single(sent);
        Assert.Equal("https://plane.example.com/machine/refresh", request.Url);
        using var posted = JsonDocument.Parse(request.Body);
        // The refresh token is the only long-lived secret on the box; it goes in the
        // body, never on a URL or in argv where `ps` would show it (§13).
        Assert.Equal("lbr_r_bbb", posted.RootElement.GetProperty("refreshToken").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_refused_refresh_returns_null_rather_than_throwing(HttpStatusCode status)
    {
        // The other half of the asymmetry: this runs on a timer inside a live daemon.
        // A revoked machine or a plane having a bad minute is a failed tick, and
        // throwing here would take down a daemon that is still supervising tasks.
        using var http = Responding(status, """{"accessToken":"x","accessExpiresAt":"2026-07-29T13:00:00+00:00"}""", out _);

        Assert.Null(await CredentialStore.RefreshAsync(http, Plane, "lbr_r_bbb", default));
    }

    [Fact]
    public async Task A_refresh_that_returns_a_null_body_yields_null()
    {
        using var http = Responding(HttpStatusCode.OK, "null", out _);

        Assert.Null(await CredentialStore.RefreshAsync(http, Plane, "lbr_r_bbb", default));
    }

    // ── Reading a file that is not what we wrote (§5) ──────────────────────────

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"machineId\": ")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("<html>error page</html>")]
    public void Load_returns_null_for_a_corrupt_file_rather_than_throwing(string contents)
    {
        // A truncated or clobbered credentials file must read as "not enrolled", not
        // take the daemon down on startup: the recovery is to enroll again, and a
        // crash loop hides that from whoever has to do it.
        var dir = FreshDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(CredentialStore.PathIn(dir), contents);

            Assert.Null(CredentialStore.Load(dir));
        }
        finally { TestKit.TryDeleteRoot(dir); }
    }

    [Fact]
    public void Load_returns_null_when_the_path_is_a_directory()
    {
        // File.Exists is false for a directory, so this takes the absent path rather
        // than the catch — asserted so the two "unreadable" shapes are both pinned.
        var dir = FreshDir();
        try
        {
            Directory.CreateDirectory(CredentialStore.PathIn(dir));

            Assert.Null(CredentialStore.Load(dir));
        }
        finally { TestKit.TryDeleteRoot(dir); }
    }

    [SkippableFact]
    public void Load_returns_null_when_the_file_cannot_be_read()
    {
        Skip.If(OperatingSystem.IsWindows(), "unix file modes are a POSIX concept");
        Skip.If(Environment.UserName == "root", "root reads regardless of mode");
        var dir = FreshDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = CredentialStore.PathIn(dir);
            File.WriteAllText(path, "{}");
            SetUnreadable(path);

            // An UnauthorizedAccessException on the credential file is the same
            // "cannot proceed, re-enroll" answer as a corrupt one.
            Assert.Null(CredentialStore.Load(dir));
        }
        finally { TestKit.TryDeleteRoot(dir); }
    }

    private static void SetUnreadable(string path)
    {
        // The static guard (not just the Skip above) is what the CA1416
        // platform-compatibility analyzer needs to allow the POSIX-only call.
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, UnixFileMode.None);
    }

    [Fact]
    public void A_credential_file_written_by_save_survives_a_round_trip_through_enrollment()
    {
        // Ties the two halves together: what EnrollAsync builds is what Save/Load
        // persists, so the enrolled record is the one the daemon reads back on boot.
        var dir = FreshDir();
        try
        {
            var enrolled = new MachineCredentialFile(
                "11111111-1111-1111-1111-111111111111", Plane, "lbr_m_aaa", AccessExp, "lbr_r_bbb", RefreshExp);

            CredentialStore.Save(dir, enrolled);

            Assert.Equal(enrolled, CredentialStore.Load(dir));
        }
        finally { TestKit.TryDeleteRoot(dir); }
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static string EnrollBody() =>
        JsonSerializer.Serialize(new
        {
            machineId = "11111111-1111-1111-1111-111111111111",
            accessToken = "lbr_m_aaa",
            accessExpiresAt = AccessExp,
            refreshToken = "lbr_r_bbb",
            refreshExpiresAt = RefreshExp,
        });

    private static string FreshDir() =>
        Path.Combine(Path.GetTempPath(), "landbridge-cred-exchange-tests", Guid.NewGuid().ToString("N"));

    private static HttpClient Responding(HttpStatusCode status, string body, out List<SentRequest> sent)
    {
        var captured = new List<SentRequest>();
        sent = captured;
        return new HttpClient(new StubHandler(captured, status, body));
    }

    private sealed record SentRequest(HttpMethod Method, string Url, string Body);

    private sealed class StubHandler(List<SentRequest> sent, HttpStatusCode status, string body)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            sent.Add(new SentRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
