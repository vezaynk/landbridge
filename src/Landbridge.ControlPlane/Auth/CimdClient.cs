using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Docket.ControlPlane.Auth;

/// <summary>
/// A Client ID Metadata Document (CIMD), as fetched from an HTTPS
/// <c>client_id</c> URL (MCP authorization spec §"Client ID Metadata Documents";
/// <c>draft-ietf-oauth-client-id-metadata-document-00</c> §4). Only the fields
/// Docket uses are modelled; unknown members are ignored. The document's own
/// <c>client_id</c> MUST equal the URL it was fetched from (§4.1), which the
/// client validates.
/// </summary>
public sealed record CimdDocument
{
    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; init; }

    [JsonPropertyName("client_uri")]
    public string? ClientUri { get; init; }

    [JsonPropertyName("redirect_uris")]
    public List<string>? RedirectUris { get; init; }

    /// <summary>SEP-837 <c>application_type</c> (e.g. <c>native</c> for CLI clients). Read for display/record; not enforced in v1.</summary>
    [JsonPropertyName("application_type")]
    public string? ApplicationType { get; init; }
}

/// <summary>Outcome of resolving a <c>client_id</c> URL to a validated CIMD document.</summary>
public abstract record CimdResult
{
    private CimdResult() { }

    /// <summary>The document was fetched, is valid JSON, and its <c>client_id</c> matches the URL exactly.</summary>
    public sealed record Ok(CimdDocument Document) : CimdResult;

    /// <summary>
    /// The client_id URL was malformed, the fetch was refused (SSRF policy) or
    /// failed, the body was not valid JSON / too large, required fields were
    /// missing, or the embedded <c>client_id</c> did not match the URL. All map
    /// to RFC 6749 §5.2 <c>invalid_client</c> at the endpoint; the reason is for
    /// the server log, never echoed verbatim to an unauthenticated caller.
    /// </summary>
    public sealed record Failed(string Reason) : CimdResult;
}

/// <summary>Fetches and validates Client ID Metadata Documents for the authorize endpoint.</summary>
public interface ICimdClient
{
    Task<CimdResult> FetchAsync(string clientId, CancellationToken ct = default);
}

/// <summary>
/// SSRF-conscious CIMD fetcher (draft §6.5–6.6). Because the authorization server
/// fetches an <em>attacker-influenced</em> URL, the fetch is fenced on every axis
/// the draft calls out:
///
/// <list type="bullet">
/// <item><b>Scheme + shape (§3):</b> <c>client_id</c> must be an absolute
///   <c>https</c> URL with a path component and no fragment or userinfo. (A
///   dev/test flag relaxes the scheme to permit a loopback <c>http</c> test
///   server — see the constructor.)</item>
/// <item><b>No redirects (§6.5):</b> <c>AllowAutoRedirect=false</c>, so a public
///   URL cannot 3xx-bounce the fetcher to an internal address.</item>
/// <item><b>Address filtering at connect time (§6.5):</b> a custom
///   <c>ConnectCallback</c> resolves the host and connects only to a
///   non-private, non-loopback, non-link-local address — and connects to that
///   <em>exact vetted IP</em>, which closes the DNS-rebinding gap between a
///   name-resolution check and the actual connect. Proxies are off, so the
///   endpoint that callback vets is always the origin.</item>
/// <item><b>Bounded (§6.6):</b> a short total timeout, plus a 5&#160;KB response
///   cap (the draft's recommended maximum) enforced by the body read itself,
///   which stops one byte past the cap and refuses the document — so a slow or
///   huge body cannot tie the endpoint up.</item>
/// </list>
///
/// <para>Residual, documented as follow-ups: HTTP cache-header-respecting caching
/// (§4.3) is not implemented — every authorize fetches fresh; and
/// <c>logo_uri</c> is neither fetched nor rendered (§6.7), which sidesteps a
/// second SSRF vector entirely for v1.</para>
/// </summary>
public sealed class CimdClient : ICimdClient
{
    /// <summary>Config key: when true, permit <c>http</c> and private/loopback CIMD hosts. Dev/test only; default false.</summary>
    public const string AllowInsecureKey = "Docket:Oauth:AllowInsecureClientMetadata";

    private const int MaxBytes = 5 * 1024; // draft §6.6 recommended cap
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _allowInsecure;

    /// <param name="allowInsecure">
    /// Relaxes the https requirement and the private/loopback address block so a
    /// test (or a trusted-LAN dev loop) can serve a CIMD document from a loopback
    /// HTTP server. <b>Never enable in production</b> — it disables the SSRF
    /// address fence. Wired from <see cref="AllowInsecureKey"/>.
    /// </param>
    public CimdClient(bool allowInsecure)
    {
        _allowInsecure = allowInsecure;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            ConnectCallback = ValidatedConnectAsync,
            // No proxy (§6.5). UseProxy defaults to true, and through a proxy
            // ValidatedConnectAsync would vet the *proxy's* endpoint instead of
            // the origin's — both the address fence and the connect-to-the-vetted-IP
            // anti-rebinding property would then apply to the wrong host. A CIMD
            // document lives on a public origin, so reach it directly or not at all.
            UseProxy = false,
        };
        _http = new HttpClient(handler)
        {
            Timeout = Timeout,
        };
    }

    public async Task<CimdResult> FetchAsync(string clientId, CancellationToken ct = default)
    {
        if (!TryValidateClientIdUrl(clientId, out var url, out var shapeError))
            return new CimdResult.Failed(shapeError);

        string body;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new CimdResult.Failed($"client_id metadata fetch returned {(int)response.StatusCode}");

            // §6.6: the cap is enforced here, on the read, because nothing else
            // would. HttpClient.MaxResponseContentBufferSize bounds only buffering
            // HttpClient does itself, which ResponseHeadersRead skips — the body
            // below is the unbuffered content stream, so the cap has to be counted
            // out byte by byte. Over-cap is refused rather than truncated: a
            // truncated prefix can still parse as a complete document.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            if (await ReadCappedAsync(stream, ct) is not { } capped)
                return new CimdResult.Failed($"client_id metadata exceeds the {MaxBytes / 1024} KB cap");
            body = capped;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Fetch failed or was refused (including the SSRF ConnectCallback
            // throwing). The draft §4.3 says abort the authorization request.
            return new CimdResult.Failed($"client_id metadata fetch failed: {ex.Message}");
        }

        CimdDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<CimdDocument>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return new CimdResult.Failed("client_id metadata is not valid JSON");
        }

        if (doc is null)
            return new CimdResult.Failed("client_id metadata is empty");

        // §4.1: the document MUST contain client_id/client_name/redirect_uris, and
        // its client_id MUST equal the fetch URL by simple string comparison.
        if (string.IsNullOrEmpty(doc.ClientId) || string.IsNullOrEmpty(doc.ClientName)
            || doc.RedirectUris is not { Count: > 0 })
            return new CimdResult.Failed("client_id metadata missing client_id, client_name, or redirect_uris");
        if (!string.Equals(doc.ClientId, clientId, StringComparison.Ordinal))
            return new CimdResult.Failed("client_id in metadata does not match the client_id URL");

        return new CimdResult.Ok(doc);
    }

    /// <summary>
    /// Reads at most <see cref="MaxBytes"/> bytes of body, or <c>null</c> if the
    /// endpoint had more to give than that (draft §6.6). Reading stops one byte past
    /// the cap, so an oversized — or endless — body is abandoned there rather than
    /// drained to learn how big it was.
    /// </summary>
    private static async Task<string?> ReadCappedAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxBytes + 1];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
                return Encoding.UTF8.GetString(buffer, 0, filled);
            filled += read;
        }

        return null;
    }

    /// <summary>
    /// Validates the <c>client_id</c> URL shape (draft §3): absolute, https (unless
    /// insecure fetching is enabled), a path component present, no fragment, no
    /// userinfo. On success yields the parsed <see cref="Uri"/>.
    /// </summary>
    private bool TryValidateClientIdUrl(string clientId, out Uri url, out string error)
    {
        url = null!;
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var parsed))
        {
            error = "client_id is not an absolute URL";
            return false;
        }

        var httpsRequired = !_allowInsecure;
        var schemeOk = parsed.Scheme == Uri.UriSchemeHttps || (!httpsRequired && parsed.Scheme == Uri.UriSchemeHttp);
        if (!schemeOk)
        {
            error = "client_id URL must use https";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "client_id URL must not contain userinfo";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "client_id URL must not contain a fragment";
            return false;
        }
        if (parsed.AbsolutePath is "" or "/")
        {
            error = "client_id URL must contain a path component";
            return false;
        }

        url = parsed;
        error = "";
        return true;
    }

    /// <summary>
    /// Resolves the target host and connects only to a permitted address, at
    /// connect time (draft §6.5). Connecting to the exact vetted IP — rather than
    /// re-resolving the name inside <see cref="HttpClient"/> — is what defeats a
    /// DNS-rebinding TOCTOU between the check and the connection.
    /// </summary>
    private async ValueTask<Stream> ValidatedConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] candidates = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);

        var target = Array.Find(candidates, a => _allowInsecure || IsPubliclyRoutable(a));
        if (target is null)
            throw new IOException(
                $"refusing to fetch client_id metadata: host '{host}' resolves only to " +
                "private, loopback, or link-local addresses (SSRF guard)");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True iff <paramref name="ip"/> is a public unicast address — i.e. not
    /// loopback, private (RFC 1918 / RFC 4193 ULA), link-local, multicast, or a
    /// wildcard/unspecified address. IPv4-mapped IPv6 is unwrapped so
    /// <c>::ffff:10.0.0.1</c> cannot smuggle a private v4 address past the check.
    /// </summary>
    private static bool IsPubliclyRoutable(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)
            || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || ip.IsIPv6Multicast || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
            || ip.IsIPv6UniqueLocal)
            return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return false;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return false;
            // 169.254.0.0/16 link-local, 100.64.0.0/10 CGNAT, 0.0.0.0/8, 224+ multicast/reserved
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            if (b[0] == 0) return false;
            if (b[0] >= 224) return false;
        }

        return true;
    }
}
