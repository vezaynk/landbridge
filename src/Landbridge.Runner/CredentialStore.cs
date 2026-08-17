using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Landbridge.Runner;

/// <summary>
/// The machine credentials landbridged persists after enrollment (spec §5, §13):
/// its machine id, the control-plane HTTP base it enrolled against, and the
/// access/refresh token pair with their expiries. The refresh token is the only
/// long-lived secret on the box; the access token is short and re-minted (§13),
/// so this record is rewritten in place on every refresh.
///
/// <para>The <see cref="ControlUrl"/> is the plane's HTTP(S) base (e.g.
/// <c>https://plane.example.com</c>) — where <c>/enroll</c> and
/// <c>/machine/refresh</c> live. It is persisted so the daemon can refresh with
/// no further argv, and so the runner WebSocket URL can be derived from it when
/// <c>LANDBRIDGE_CONTROL_URL</c> is not set (see <see cref="CredentialStore.DeriveRunnerWsUrl"/>).</para>
/// </summary>
internal sealed record MachineCredentialFile(
    string MachineId,
    string ControlUrl,
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);

/// <summary>The POST /enroll body landbridged sends (§11, §13). os is auto-filled.</summary>
internal sealed record EnrollRequest(
    string EnrollmentToken, string Name, string Purpose, string Os, string PermissionLevel);

/// <summary>The 200 body from /enroll: a machine identity plus its token pair (§5).</summary>
internal sealed record EnrollResponse(
    string MachineId,
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);

/// <summary>The POST /machine/refresh body: the refresh token (§13).</summary>
internal sealed record RefreshRequest(string RefreshToken);

/// <summary>The 200 body from /machine/refresh: a fresh access token (§13).</summary>
internal sealed record RefreshResponse(string AccessToken, DateTimeOffset AccessExpiresAt);

/// <summary>
/// Source-generated JSON metadata for the credentials file and the bootstrap
/// wire (§5, §13). camelCase to match the control plane's Web-default JSON on
/// <c>/enroll</c> and <c>/machine/refresh</c>; kept separate from
/// <see cref="RunnerJsonContext"/> (which is snake_case for the config document)
/// so landbridged stays AOT-clean with no reflection-based serializer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MachineCredentialFile))]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(RefreshResponse))]
internal sealed partial class CredentialsJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes landbridged's <c>credentials.json</c> and drives the enrollment
/// and refresh HTTP exchanges (spec §5 Bootstrap, §13). Writes are atomic
/// (temp + rename) and owner-only (0600) — the file holds a live machine
/// credential, exactly like the worker <c>mcp.json</c> the supervisor writes.
/// </summary>
internal static class CredentialStore
{
    public const string FileName = "credentials.json";

    /// <summary>The credentials file within a state dir.</summary>
    public static string PathIn(string stateDir) => Path.Combine(stateDir, FileName);

    /// <summary>
    /// Where credentials live when no <c>--state-dir</c> is given. Pure over its
    /// inputs so the resolution is testable without touching the environment:
    /// an explicit <paramref name="argStateDir"/> wins, then
    /// <c>LANDBRIDGE_STATE_DIR</c>, then <c>$XDG_STATE_HOME/landbridge</c>, else
    /// <c>~/.landbridge</c>.
    /// </summary>
    public static string ResolveStateDir(
        string? argStateDir, string? envStateDir, string? xdgStateHome, string homeDir)
    {
        if (!string.IsNullOrWhiteSpace(argStateDir))
            return argStateDir;
        if (!string.IsNullOrWhiteSpace(envStateDir))
            return envStateDir;
        if (!string.IsNullOrWhiteSpace(xdgStateHome))
            return Path.Combine(xdgStateHome, "landbridge");
        return Path.Combine(homeDir, ".landbridge");
    }

    /// <summary>Resolves the default state dir from the current environment.</summary>
    public static string ResolveStateDir(string? argStateDir) =>
        ResolveStateDir(
            argStateDir,
            Environment.GetEnvironmentVariable("LANDBRIDGE_STATE_DIR"),
            Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Loads persisted credentials, or null if the file is absent or unreadable/corrupt.</summary>
    public static MachineCredentialFile? Load(string stateDir)
    {
        var path = PathIn(stateDir);
        if (!File.Exists(path))
            return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CredentialsJsonContext.Default.MachineCredentialFile);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Persists credentials atomically and owner-only (§13). The temp file is
    /// chmod 0600 <i>before</i> the rename, so the secret never lands at its
    /// final name world-readable, and the rename is atomic on POSIX — a
    /// concurrent reader sees either the old file or the new one, never a
    /// half-written one.
    /// </summary>
    public static void Save(string stateDir, MachineCredentialFile creds)
    {
        Directory.CreateDirectory(stateDir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stateDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = PathIn(stateDir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(creds, CredentialsJsonContext.Default.MachineCredentialFile);
        File.WriteAllText(tmp, json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// The runner WebSocket URL derived from the plane's HTTP base (§10): the
    /// scheme flips http→ws / https→wss and the path becomes <c>/runner</c>.
    /// landbridged dials <c>LANDBRIDGE_CONTROL_URL</c> when set; this is the default when
    /// it is not, so a file-enrolled daemon needs no second URL.
    /// </summary>
    public static Uri DeriveRunnerWsUrl(string httpBase)
    {
        var uri = new Uri(httpBase);
        var b = new UriBuilder(uri)
        {
            Scheme = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = uri.AbsolutePath.TrimEnd('/') + "/runner",
            Query = "",
            Fragment = "",
        };
        // A base with no explicit port carries the http/https default in the
        // builder; drop it after the scheme flip so we don't emit :443/:80 on the
        // ws(s) URL. An explicit non-default port is preserved.
        if (uri.IsDefaultPort)
            b.Port = -1;
        return b.Uri;
    }

    /// <summary>
    /// Exchanges an enrollment token for machine credentials at
    /// <c>{controlUrl}/enroll</c> and returns the file record (stamped with
    /// <paramref name="controlUrl"/> for later refresh). Throws
    /// <see cref="EnrollmentException"/> on a non-200 response so the enroll
    /// command can print a clear message and exit non-zero.
    /// </summary>
    public static async Task<MachineCredentialFile> EnrollAsync(
        HttpClient http, string controlUrl, EnrollRequest request, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync(
            CombineBase(controlUrl, "enroll"), request, CredentialsJsonContext.Default.EnrollRequest, ct);
        if (!resp.IsSuccessStatusCode)
            throw new EnrollmentException(
                $"enrollment refused ({(int)resp.StatusCode} {resp.StatusCode}) — the token may be used, expired, or invalid");

        var body = await resp.Content.ReadFromJsonAsync(CredentialsJsonContext.Default.EnrollResponse, ct)
            ?? throw new EnrollmentException("enrollment returned an empty body");

        return new MachineCredentialFile(
            body.MachineId, controlUrl, body.AccessToken, body.AccessExpiresAt,
            body.RefreshToken, body.RefreshExpiresAt);
    }

    /// <summary>
    /// Mints a fresh access token at <c>{controlUrl}/machine/refresh</c>. Returns
    /// null on any non-200 (a dead refresh or a revoked machine) so the refresh
    /// loop treats "the plane said no" as a failed tick rather than a crash.
    /// </summary>
    public static async Task<RefreshResponse?> RefreshAsync(
        HttpClient http, string controlUrl, string refreshToken, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync(
            CombineBase(controlUrl, "machine/refresh"),
            new RefreshRequest(refreshToken), CredentialsJsonContext.Default.RefreshRequest, ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        return await resp.Content.ReadFromJsonAsync(CredentialsJsonContext.Default.RefreshResponse, ct);
    }

    private static string CombineBase(string controlUrl, string relative) =>
        controlUrl.TrimEnd('/') + "/" + relative;
}

/// <summary>Thrown when the enrollment exchange fails; carries an operator-facing message.</summary>
internal sealed class EnrollmentException(string message) : Exception(message);
