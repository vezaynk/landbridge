using System.Text;

namespace Landbridge.Preview;

/// <summary>
/// The minimal slice of an HTTP/1.1 request the preview frontend needs to
/// <b>route</b> a browser connection (spec §8.4) — nothing more. The frontend does
/// not parse or re-emit the request; it reads only up to the blank line that ends
/// the head, keeps those bytes verbatim to replay into the tunnel, and then
/// byte-splices the rest of the connection. That is what guarantees cookies and
/// absolute paths in the served app are never rewritten (§8.4).
///
/// <para>Routing is strictly by the <c>Host</c> header (§8.4). The operator session
/// for a gated preview is read from an <c>Authorization: Bearer</c> header or the
/// <c>landbridge_session</c> cookie and forwarded to the control plane, which owns the
/// actual validation.</para>
/// </summary>
public sealed class HttpRequestHead
{
    /// <summary>
    /// The dashboard/operator session cookie (must match <c>DashboardAuth.CookieName</c>).
    ///
    /// <para><b>Cross-origin dependency (§8.4 gated auth handoff).</b> For a gated
    /// preview to admit a browser via this cookie, the browser must actually send it
    /// to <c>*.preview.&lt;domain&gt;</c> — which only happens if the §12 dashboard sets
    /// the operator session cookie with a parent <c>Domain</c> (e.g. <c>Domain=.&lt;domain&gt;</c>)
    /// covering both the dashboard origin and the preview wildcard origin. The
    /// dashboard today scopes it host-only (<c>Path=/dashboard</c>, no Domain), so
    /// widening it is a §12 change owned by the dashboard worktree. Until then, the
    /// bearer path below (a Lead presenting <c>Authorization: Bearer</c>) is the
    /// working gated-access mechanism; the frontend forwards whichever arrives.</para>
    /// </summary>
    public const string SessionCookieName = "landbridge_session";

    /// <summary>The per-label preview session cookie the frontend sets on the preview origin (§8.4 gated flow).</summary>
    public const string PreviewCookieName = "landbridge_preview";

    /// <summary>The query parameter the dashboard confirm redirects back with (§8.4 gated flow).</summary>
    public const string PreviewCodeQueryKey = "landbridge_preview_code";

    private const int MaxHeadBytes = 64 * 1024;
    private static readonly byte[] HeadTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>The raw head bytes, up to and including the terminating blank line — replayed into the tunnel verbatim.</summary>
    public required byte[] RawHead { get; init; }

    /// <summary>Any bytes read past the head (a pipelined body/first frame) — replayed after <see cref="RawHead"/>.</summary>
    public required byte[] Extra { get; init; }

    /// <summary>The <c>Host</c> header value (host[:port]), or null if absent.</summary>
    public string? Host { get; init; }

    /// <summary>The request-line target (path + query), e.g. <c>/deep/path?x=1</c>, or "/" if unparseable.</summary>
    public string Target { get; init; } = "/";

    /// <summary>The operator session for gating: an <c>Authorization: Bearer</c> or the <c>landbridge_session</c> cookie, or null.</summary>
    public string? OperatorSession { get; init; }

    /// <summary>The bearer value if the request carried <c>Authorization: Bearer</c> — the tooling path (a browser has none).</summary>
    public string? Bearer { get; init; }

    /// <summary>The per-label preview session from the <c>landbridge_preview</c> cookie (§8.4 gated flow), or null.</summary>
    public string? PreviewCookie { get; init; }

    /// <summary>The one-time preview-auth code the dashboard redirected back with (§8.4), or null.</summary>
    public string? PreviewCode { get; init; }

    /// <summary>Whether the request is a WebSocket upgrade — informational (the splice handles it transparently).</summary>
    public bool IsWebSocketUpgrade { get; init; }

    /// <summary>
    /// Read and parse a request head from <paramref name="stream"/>, or null if the
    /// connection closed or exceeded <see cref="MaxHeadBytes"/> before a complete
    /// head arrived. <paramref name="ct"/> should carry the head-read timeout.
    /// </summary>
    public static async Task<HttpRequestHead?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var acc = new MemoryStream();
        while (acc.Length < MaxHeadBytes)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, ct);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                return null;
            }
            if (read == 0)
                return null; // closed before a full head

            acc.Write(buffer, 0, read);
            var span = acc.GetBuffer().AsSpan(0, (int)acc.Length);
            var idx = span.IndexOf(HeadTerminator);
            if (idx < 0)
                continue;

            var headLen = idx + HeadTerminator.Length;
            var all = acc.GetBuffer();
            var rawHead = all[..headLen];
            var extra = all[headLen..(int)acc.Length];
            return Parse(rawHead, extra);
        }

        return null; // head too large
    }

    private static HttpRequestHead Parse(byte[] rawHead, byte[] extra)
    {
        // Latin-1 keeps every header byte 1:1 so RawHead stays the source of truth
        // for what goes on the wire; we only read a few header values from the text.
        var text = Encoding.Latin1.GetString(rawHead);
        var lines = text.Split("\r\n");

        // lines[0] is the request line: METHOD SP request-target SP HTTP/1.1.
        var requestLineParts = lines[0].Split(' ');
        var target = requestLineParts.Length >= 2 ? requestLineParts[1] : "/";

        string? host = null;
        string? bearer = null;
        string? cookieSession = null;
        string? previewCookie = null;
        var isUpgrade = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                break;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                host = value;
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                     && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                bearer = value["Bearer ".Length..].Trim();
            else if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                cookieSession = ReadCookie(value, SessionCookieName);
                previewCookie = ReadCookie(value, PreviewCookieName);
            }
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                     && value.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                isUpgrade = true;
        }

        return new HttpRequestHead
        {
            RawHead = rawHead,
            Extra = extra,
            Host = host,
            Target = target,
            Bearer = bearer,
            // A bearer (a Lead consuming programmatically) takes precedence over the
            // browser cookie, mirroring DashboardAuth's resolution order (§12).
            OperatorSession = bearer ?? cookieSession,
            PreviewCookie = previewCookie,
            PreviewCode = ReadQueryParam(target, PreviewCodeQueryKey),
            IsWebSocketUpgrade = isUpgrade,
        };
    }

    /// <summary>
    /// The request head with all operator auth material removed — <c>Authorization</c>
    /// dropped entirely, and the <c>landbridge_session</c> + <c>landbridge_preview</c> cookies
    /// removed from the <c>Cookie</c> header (§8.4). This is the ONE deliberate
    /// exception to the never-rewrite rule (§8.4): the frontend splices request bytes
    /// verbatim into team workload code, so an operator's session token or the
    /// per-label preview cookie must never reach that code — it would be exfiltratable
    /// by whatever the previewed service does with its request headers. A one-time
    /// head edit, before the splice; the body and every non-auth header are untouched.
    /// </summary>
    public byte[] StripAuthMaterial()
    {
        var text = Encoding.Latin1.GetString(RawHead);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headBlock = end >= 0 ? text[..end] : text;
        var lines = headBlock.Split("\r\n");

        var sb = new StringBuilder();
        sb.Append(lines[0]).Append("\r\n"); // request line, verbatim
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            var name = colon > 0 ? line[..colon].Trim() : "";

            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                continue; // strip entirely

            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                var kept = StripCookies(line[(colon + 1)..], SessionCookieName, PreviewCookieName);
                if (kept is not null)
                    sb.Append("Cookie: ").Append(kept).Append("\r\n");
                continue; // drop the header entirely when nothing remains
            }

            sb.Append(line).Append("\r\n");
        }
        sb.Append("\r\n"); // terminating blank line
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The request target with the one-time <c>landbridge_preview_code</c> query parameter removed (§8.4 clean redirect).</summary>
    public string TargetWithoutCode()
    {
        var q = Target.IndexOf('?');
        if (q < 0)
            return Target;
        var path = Target[..q];
        var kept = Target[(q + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith(PreviewCodeQueryKey + "=", StringComparison.Ordinal))
            .ToArray();
        return kept.Length == 0 ? path : $"{path}?{string.Join('&', kept)}";
    }

    private static string? ReadQueryParam(string target, string key)
    {
        var q = target.IndexOf('?');
        if (q < 0)
            return null;
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            if (pair[..eq].Equals(key, StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    /// <summary>Rebuild a <c>Cookie</c> header value dropping the named cookies; null when none remain.</summary>
    private static string? StripCookies(string header, params string[] drop)
    {
        var kept = header.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(kv =>
            {
                var eq = kv.IndexOf('=');
                var name = eq > 0 ? kv[..eq].Trim() : kv;
                return !drop.Contains(name, StringComparer.Ordinal);
            })
            .ToArray();
        return kept.Length == 0 ? null : string.Join("; ", kept);
    }

    /// <summary>Pull one cookie's value out of a <c>Cookie:</c> header (<c>a=1; b=2</c>).</summary>
    private static string? ReadCookie(string header, string name)
    {
        foreach (var part in header.Split(';'))
        {
            var kv = part.Trim();
            var eq = kv.IndexOf('=');
            if (eq <= 0)
                continue;
            if (kv[..eq].Trim().Equals(name, StringComparison.Ordinal))
                return kv[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// The preview label for a Host under <paramref name="domain"/>: the single
    /// leftmost label of <c>{label}.{domain}</c>. Null when the Host is absent,
    /// does not sit directly under the domain, or carries more than one label —
    /// the label is one opaque token, never a dotted structure (§8.4).
    /// </summary>
    public string? LabelUnder(string domain)
    {
        var host = Host;
        if (string.IsNullOrEmpty(host))
            return null;

        // Drop any port and normalize case (DNS is case-insensitive).
        var colon = host.IndexOf(':');
        if (colon >= 0)
            host = host[..colon];
        host = host.Trim().ToLowerInvariant();
        var suffix = "." + domain.Trim().ToLowerInvariant();

        if (!host.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var label = host[..^suffix.Length];
        if (label.Length == 0 || label.Contains('.'))
            return null;
        return label;
    }
}
