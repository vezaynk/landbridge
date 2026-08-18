namespace Landbridge.Runner.Tests;

/// <summary>
/// landbridged's persisted machine credentials (spec §5, §13): the round-trip is
/// AOT-safe (source-gen JSON), the file is written atomically and owner-only
/// (0600), the state-dir resolution honours its documented precedence, and the
/// runner WebSocket URL derives correctly from the enrolled HTTP base.
/// </summary>
public class CredentialStoreTests
{
    private static readonly DateTimeOffset AccessExp = DateTimeOffset.Parse("2026-07-29T13:00:00+00:00");
    private static readonly DateTimeOffset RefreshExp = DateTimeOffset.Parse("2026-10-27T12:00:00+00:00");

    private static MachineCredentialFile Sample(string access = "lbr_m_aaa", string refresh = "lbr_r_bbb") =>
        new("11111111-1111-1111-1111-111111111111", "https://plane.example.com", access, AccessExp, refresh, RefreshExp);

    private static string FreshDir() =>
        Path.Combine(Path.GetTempPath(), "landbridge-cred-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_then_load_round_trips_exactly()
    {
        var dir = FreshDir();
        try
        {
            var creds = Sample();
            CredentialStore.Save(dir, creds);
            Assert.Equal(creds, CredentialStore.Load(dir)); // records → value equality, incl. expiries
        }
        finally { TestKit.TryDeleteRoot(dir); }
    }

    [Fact]
    public void Load_returns_null_when_the_file_is_absent()
    {
        var dir = FreshDir();
        Assert.Null(CredentialStore.Load(dir)); // nothing written, dir does not even exist yet
    }

    [SkippableFact]
    public void Save_writes_the_file_owner_only()
    {
        Skip.If(OperatingSystem.IsWindows(), "unix file modes are a POSIX concept");
        var dir = FreshDir();
        try
        {
            CredentialStore.Save(dir, Sample());
            AssertOwnerOnly(CredentialStore.PathIn(dir)); // 0600 — a live credential
        }
        finally { TestKit.TryDeleteRoot(dir); }
    }

    private static void AssertOwnerOnly(string path)
    {
        // The static guard (not just the Skip above) is what the CA1416
        // platform-compatibility analyzer needs to allow the POSIX-only call.
        if (OperatingSystem.IsWindows())
            return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Save_is_atomic_overwrite_and_leaves_no_temp_files()
    {
        var dir = FreshDir();
        try
        {
            CredentialStore.Save(dir, Sample(access: "lbr_m_first"));
            CredentialStore.Save(dir, Sample(access: "lbr_m_second"));

            Assert.Equal("lbr_m_second", CredentialStore.Load(dir)!.AccessToken); // the rename replaced the old file
            Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));                      // the temp was consumed by the rename
            Assert.Single(Directory.GetFiles(dir));                               // only credentials.json remains
        }
        finally { TestKit.TryDeleteRoot(dir); }
    }

    [Theory]
    [InlineData("/explicit", "/env", "/xdg", "/home/user")]  // --state-dir wins (verbatim)
    [InlineData(null, "/env", "/xdg", "/home/user")]         // then LANDBRIDGE_STATE_DIR (verbatim)
    [InlineData(null, null, "/xdg", "/home/user")]           // then $XDG_STATE_HOME/landbridge
    [InlineData(null, null, null, "/home/user")]             // else ~/.landbridge
    public void ResolveStateDir_follows_documented_precedence(
        string? arg, string? env, string? xdg, string home)
    {
        // Mirror the production contract exactly, OS-agnostically: an explicit
        // arg/env directory is returned verbatim; the xdg/home fallbacks are
        // Path.Combine'd (so the separator matches the OS, unlike a hardcoded '/').
        var expected =
            arg is not null ? arg
            : env is not null ? env
            : xdg is not null ? Path.Combine(xdg, "landbridge")
            : Path.Combine(home, ".landbridge");

        Assert.Equal(expected, CredentialStore.ResolveStateDir(arg, env, xdg, home));
    }

    [Theory]
    [InlineData("https://plane.example.com", "wss://plane.example.com/runner")]
    [InlineData("https://plane.example.com/", "wss://plane.example.com/runner")]
    [InlineData("http://localhost:5000", "ws://localhost:5000/runner")]
    [InlineData("http://127.0.0.1:8080/", "ws://127.0.0.1:8080/runner")]
    public void DeriveRunnerWsUrl_flips_scheme_and_appends_runner(string httpBase, string expected)
    {
        Assert.Equal(expected, CredentialStore.DeriveRunnerWsUrl(httpBase).ToString());
    }
}
