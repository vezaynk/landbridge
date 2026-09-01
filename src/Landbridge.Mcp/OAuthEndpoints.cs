using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Landbridge.ControlPlane.Auth;
using Microsoft.AspNetCore.WebUtilities;

namespace Landbridge.Mcp;

/// <summary>
/// The OAuth 2.1 authorization-code flow (spec §5): <c>/oauth/authorize</c> and
/// <c>/oauth/token</c>, the front door a human — and an MCP client like Claude
/// Code acting for one — walks through to obtain the existing opaque
/// <c>lbr_h_</c> human session token. Plain anonymous HTTP in the narrow,
/// non-MCP style of <see cref="EnrollmentEndpoints"/>; the endpoints are the
/// transport shell and all the credential logic lives in the control-plane
/// services (<see cref="OAuthAuthorizationCodeService"/>,
/// <see cref="TokenService"/>, <see cref="IOperatorVerifier"/>,
/// <see cref="ICimdClient"/>).
///
/// <para><b>The access token is unchanged.</b> A completed flow calls
/// <see cref="TokenService.IssueHumanSessionAsync"/> — the same seam a §5 human
/// session has always been minted through — so the token the client receives is
/// the ordinary opaque human session, validated by the unchanged bearer path in
/// <c>LandbridgeAuthenticationHandler</c>. This endpoint surface only adds the OAuth
/// wire protocol in front of that mint.</para>
///
/// <para>Every RFC rule enforced is cited at the point it is applied:
/// OAuth 2.1 / RFC 6749 (authorization-code grant, §4.1; error responses, §4.1.2.1
/// and §5.2), RFC 7636 (PKCE, S256-only), RFC 8707 (the <c>resource</c> audience
/// parameter), RFC 8252 §7.3 (loopback redirect ports), and the CIMD draft
/// (URL <c>client_id</c>, redirect-URI exact match).</para>
/// </summary>
public static class OAuthEndpoints
{
    /// <summary>
    /// The one <c>invalid_grant</c> description the token endpoint ever emits. Every
    /// way a code exchange can fail — unknown, expired, replayed, or bound to a
    /// different client/redirect/resource/verifier — answers with this exact string,
    /// so the response distinguishes nothing (RFC 6749 §5.2).
    /// </summary>
    private const string InvalidGrantDescription =
        "the authorization code is invalid, expired, already used, or does not match this request";

    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by construction: a client reaches these before it has a token.
        app.MapGet("/oauth/authorize", (HttpContext http, IOperatorVerifier verifier, ICimdClient cimd,
            OAuthAuthorizationCodeService codes, OAuthServerConfig server, ILoggerFactory logs,
            OperatorAttemptLimiter attempts, CancellationToken ct) =>
            AuthorizeAsync(http, isPost: false, verifier, cimd, codes, server, logs, attempts, ct));

        app.MapPost("/oauth/authorize", (HttpContext http, IOperatorVerifier verifier, ICimdClient cimd,
            OAuthAuthorizationCodeService codes, OAuthServerConfig server, ILoggerFactory logs,
            OperatorAttemptLimiter attempts, CancellationToken ct) =>
            AuthorizeAsync(http, isPost: true, verifier, cimd, codes, server, logs, attempts, ct));

        app.MapPost("/oauth/token", TokenAsync);
        return app;
    }

    // ── /oauth/authorize ──────────────────────────────────────────────────────

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http, bool isPost, IOperatorVerifier verifier, ICimdClient cimd,
        OAuthAuthorizationCodeService codes, OAuthServerConfig server, ILoggerFactory logs,
        OperatorAttemptLimiter attempts, CancellationToken ct)
    {
        var log = logs.CreateLogger("Landbridge.Mcp.OAuth");

        // Fail-closed (§5, mirrors RelayValidationEndpoints' bearer): with no
        // operator credential configured the authorization server can verify no
        // one, so it mints nothing and says so loudly rather than handing out a
        // human session to an unauthenticated caller.
        if (!verifier.IsConfigured)
        {
            log.LogError(
                "/oauth/authorize was called but no operator passphrase is configured ({Key}); refusing " +
                "every authorization. Set an Identity PBKDF2 hash of the operator passphrase to open the front door.",
                ConfiguredOperatorVerifier.PassphraseHashKey);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var p = isPost ? ReadParams(await http.Request.ReadFormAsync(ct)) : ReadParams(http.Request.Query);

        // §4.1.2.1: client_id and redirect_uri problems MUST NOT redirect (the
        // redirect target is not yet trustworthy) — inform the human directly.
        if (string.IsNullOrEmpty(p.ClientId))
            return ErrorPage(StatusCodes.Status400BadRequest, "invalid_request", "client_id is required.");
        if (string.IsNullOrEmpty(p.RedirectUri))
            return ErrorPage(StatusCodes.Status400BadRequest, "invalid_request", "redirect_uri is required.");

        // CIMD: client_id is an HTTPS URL; fetch and validate its metadata document
        // (SSRF-fenced in CimdClient). A failure is invalid_client — no redirect,
        // because we cannot trust the presented redirect_uri without the document.
        var cimdResult = await cimd.FetchAsync(p.ClientId, ct);
        if (cimdResult is not CimdResult.Ok ok)
        {
            var reason = ((CimdResult.Failed)cimdResult).Reason;
            log.LogWarning("rejecting authorize: client_id {ClientId} metadata invalid: {Reason}", p.ClientId, reason);
            return ErrorPage(StatusCodes.Status400BadRequest, "invalid_client",
                "The client_id metadata document could not be fetched or validated.");
        }
        var doc = ok.Document;

        // The presented redirect_uri must be registered in the metadata — exact
        // match (CIMD draft §4.5 / RFC 9700), with the RFC 8252 §7.3 loopback
        // port exception for CLI clients. A mismatch MUST NOT redirect.
        if (!RedirectUriIsAllowed(p.RedirectUri, doc.RedirectUris!))
        {
            log.LogWarning("rejecting authorize: redirect_uri {RedirectUri} not registered for {ClientId}",
                p.RedirectUri, p.ClientId);
            return ErrorPage(StatusCodes.Status400BadRequest, "invalid_request",
                "The redirect_uri is not registered for this client.");
        }

        // ── redirect_uri is now trusted: remaining protocol errors redirect back
        //    to it with error + verbatim state (RFC 6749 §4.1.2.1). ──

        if (p.ResponseType != "code")
            return RedirectError(p.RedirectUri, "unsupported_response_type", p.State);

        // OAuth 2.1: PKCE is mandatory and S256-only. A missing challenge or a
        // 'plain' (or any non-S256) method is rejected — no downgrade.
        if (!Pkce.IsSupportedMethod(p.CodeChallengeMethod) || string.IsNullOrEmpty(p.CodeChallenge))
            return RedirectError(p.RedirectUri, "invalid_request", p.State,
                "PKCE is required with code_challenge_method=S256.");

        // RFC 8707 §2: if the client indicates a resource it must be this
        // Instance's canonical id. Single instance = single audience, so equality
        // is the whole audience check. RFC 8707 §2.2 error code is invalid_target.
        if (p.Resource is not null && !server.ResourceMatches(p.Resource))
            return RedirectError(p.RedirectUri, "invalid_target", p.State,
                "resource does not identify this server.");

        // GET renders the consent page; POST is the human submitting it.
        if (!isPost)
            return ConsentPage(doc, p);

        // POST: verify the operator passphrase (the human-verification seam).
        var clientKey = http.Connection.RemoteIpAddress?.ToString();
        if (!attempts.TryAcquire(clientKey))
            return ConsentPage(doc, p, "Too many attempts. Try again in a minute.");
        var passphrase = http.Request.Form["passphrase"].ToString();
        if (!verifier.Verify(passphrase))
            return ConsentPage(doc, p, "Incorrect operator passphrase.");

        // Verified. Mint a single-use code bound to {client_id, redirect_uri,
        // code_challenge, resource} and 302 it back with the verbatim state.
        var issued = await codes.MintAsync(p.ClientId, p.RedirectUri, p.CodeChallenge!, p.Resource, p.Scope, ct);
        log.LogInformation("issued authorization code to client {ClientId}", p.ClientId);
        return RedirectSuccess(p.RedirectUri, issued.Code, p.State);
    }

    // ── /oauth/token ────────────────────────────────────────────────────────────

    private static async Task<IResult> TokenAsync(
        HttpContext http, OAuthAuthorizationCodeService codes, TokenService tokens,
        OAuthServerConfig server, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        var log = logs.CreateLogger("Landbridge.Mcp.OAuth");

        if (!http.Request.HasFormContentType)
            return TokenError("invalid_request", "token requests are application/x-www-form-urlencoded");

        var form = await http.Request.ReadFormAsync(ct);

        // RFC 6749 §4.1.3: only the authorization_code grant is supported here.
        if (form["grant_type"].ToString() != "authorization_code")
            return TokenError("unsupported_grant_type", "grant_type must be authorization_code");

        var code = form["code"].ToString();
        var clientId = form["client_id"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        var codeVerifier = form["code_verifier"].ToString();
        var resource = NullIfEmpty(form["resource"].ToString());

        // Public clients (token_endpoint_auth_method=none): client_id is required
        // (RFC 6749 §4.1.3) but there is no client secret to check.
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(clientId)
            || string.IsNullOrEmpty(redirectUri) || string.IsNullOrEmpty(codeVerifier))
            return TokenError("invalid_request", "code, client_id, redirect_uri, and code_verifier are required");

        var result = await codes.ConsumeAsync(code, clientId, redirectUri, codeVerifier, resource, server, ct);
        if (result is CodeExchangeResult.InvalidGrant invalid)
        {
            // ConsumeAsync's per-mismatch reason is a server-side diagnostic, not a
            // wire value: the caller here is an unauthenticated public client, and
            // telling it *which* bound value failed turns this endpoint into an
            // enumeration oracle (an attacker holding a stolen code could probe for
            // the client_id and redirect_uri it was bound to). Same split as the CIMD
            // failure above — log the detail, answer with one constant description.
            log.LogWarning("rejecting token exchange for client {ClientId}: {Reason}", clientId, invalid.Reason);
            return TokenError("invalid_grant", InvalidGrantDescription);
        }

        var scope = ((CodeExchangeResult.Exchanged)result).Scope;

        // The one integration point: a verified authorization becomes the existing
        // opaque human session token (§5). Everything above is the OAuth wrapper;
        // this is the unchanged mint.
        var session = await tokens.IssueHumanSessionAsync(ct);
        var expiresIn = session.ExpiresAt is { } exp
            ? Math.Max(0, (int)(exp - clock.GetUtcNow()).TotalSeconds)
            : (int)TokenService.HumanSessionTtl.TotalSeconds;

        // RFC 6749 §5.1: token responses MUST NOT be cached.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        return Results.Json(new TokenResponse(session.Token, "Bearer", expiresIn, scope));
    }

    // ── Redirect-URI validation (CIMD §4.5 + RFC 8252 §7.3) ───────────────────

    /// <summary>
    /// True iff <paramref name="presented"/> is an acceptable redirect target for
    /// a client whose CIMD document registered <paramref name="registered"/>. The
    /// primary rule is exact string match (CIMD draft §4.5 / RFC 9700). The one
    /// exception is RFC 8252 §7.3: a native/CLI client's loopback redirect may use
    /// any port, so a presented loopback URI matches a registered loopback URI
    /// when everything <em>but</em> the port is equal — scheme (http), a loopback
    /// host, and the exact path. The loopback host may itself differ among the
    /// loopback set ({127.0.0.1, ::1, localhost}), since a client cannot know which
    /// the OS will hand it.
    /// </summary>
    private static bool RedirectUriIsAllowed(string presented, IReadOnlyList<string> registered)
    {
        foreach (var reg in registered)
            if (string.Equals(presented, reg, StringComparison.Ordinal))
                return true; // exact match — the default and strictest path

        if (!TryParseLoopback(presented, out var presentedPath))
            return false;

        foreach (var reg in registered)
            if (TryParseLoopback(reg, out var regPath)
                && string.Equals(presentedPath, regPath, StringComparison.Ordinal))
                return true; // loopback, same path, any port (RFC 8252 §7.3)

        return false;
    }

    /// <summary>
    /// Parses an <c>http</c> loopback redirect URI (RFC 8252 §7.3 hosts:
    /// 127.0.0.1, ::1, or localhost), yielding its path+query for a port-agnostic
    /// compare. Anything else — https, a non-loopback host, a malformed URI — is
    /// not a loopback redirect.
    /// </summary>
    private static bool TryParseLoopback(string uri, out string pathAndQuery)
    {
        pathAndQuery = "";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp)
            return false;

        var host = u.Host;
        var isLoopback = host is "127.0.0.1" or "::1" or "[::1]" or "localhost"
            || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (!isLoopback)
            return false;

        pathAndQuery = u.PathAndQuery;
        return true;
    }

    // ── Request parsing ───────────────────────────────────────────────────────

    private readonly record struct AuthorizeParams(
        string? ResponseType, string? ClientId, string? RedirectUri,
        string? CodeChallenge, string? CodeChallengeMethod, string? State, string? Scope, string? Resource);

    private static AuthorizeParams ReadParams(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> src)
    {
        var map = src.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
        string? Get(string k) => map.TryGetValue(k, out var v) ? NullIfEmpty(v) : null;
        return new AuthorizeParams(
            Get("response_type"), Get("client_id"), Get("redirect_uri"),
            Get("code_challenge"), Get("code_challenge_method"), Get("state"), Get("scope"), Get("resource"));
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    // ── Responses ─────────────────────────────────────────────────────────────

    /// <summary>302 back to the validated redirect_uri with the code and verbatim state (RFC 6749 §4.1.2).</summary>
    private static IResult RedirectSuccess(string redirectUri, string code, string? state)
    {
        var query = new Dictionary<string, string?> { ["code"] = code };
        if (state is not null) query["state"] = state; // echoed exactly, never altered
        return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, query));
    }

    /// <summary>302 back to the validated redirect_uri with an RFC 6749 §4.1.2.1 error and verbatim state.</summary>
    private static IResult RedirectError(string redirectUri, string error, string? state, string? description = null)
    {
        var query = new Dictionary<string, string?> { ["error"] = error };
        if (description is not null) query["error_description"] = description;
        if (state is not null) query["state"] = state;
        return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, query));
    }

    /// <summary>
    /// RFC 6749 §5.2 token error: JSON body, HTTP 400, no detail beyond the code
    /// and a terse reason. Every error this endpoint emits (invalid_request,
    /// unsupported_grant_type, invalid_grant) is a 400; there is no invalid_client
    /// path because the token endpoint uses public clients (auth method "none").
    /// </summary>
    private static IResult TokenError(string error, string description) =>
        Results.Json(new TokenErrorResponse(error, description), statusCode: StatusCodes.Status400BadRequest);

    private static IResult ErrorPage(int status, string error, string message) =>
        Results.Content(BuildErrorHtml(error, message), "text/html; charset=utf-8", Encoding.UTF8, status);

    /// <summary>
    /// The consent page (§5): minimal, self-contained (no external assets), showing
    /// who is asking, where the grant would go, the requested scope, and the operator
    /// passphrase field. All original authorize parameters ride hidden inputs so the
    /// POST re-validates the identical request.
    ///
    /// <para><b>What the human is shown is chosen by integrity, not by prettiness.</b>
    /// Of everything in a CIMD document only <c>client_id</c> is verified: the fetcher
    /// enforces that the document's own <c>client_id</c> equals the URL it was fetched
    /// from (<see cref="CimdClient.FetchAsync"/>, CIMD draft §4.1), so an attacker
    /// cannot present a <c>client_id</c> it does not control the origin of.
    /// <c>client_name</c> and <c>client_uri</c> are ordinary document body — a
    /// malicious client is free to claim "Claude Code" at "claude.ai" while listing
    /// its own <c>redirect_uris</c>. So the identity line is derived from
    /// <c>client_id</c> alone and shown alongside the <c>redirect_uri</c> the code
    /// would be sent to (CIMD draft §6.4/§6.7); the self-reported name and site are
    /// rendered only as explicitly-labelled, unverified decoration.</para>
    ///
    /// <para>Every client-supplied value is HTML-encoded — the CIMD document is
    /// attacker-influenced, so none of it may reach the page unescaped.</para>
    /// </summary>
    private static IResult ConsentPage(CimdDocument doc, AuthorizeParams p, string? error = null)
    {
        var e = HtmlEncoder.Default;
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>Authorize Landbridge access</title>");
        sb.Append("<style>body{font:16px system-ui,sans-serif;max-width:34rem;margin:3rem auto;padding:0 1rem;color:#111}"
            + "h1{font-size:1.3rem}.client{background:#f4f4f5;border-radius:.5rem;padding:1rem;margin:1rem 0}"
            + ".client b{font-size:1.05rem}.host{color:#555;font-size:.9rem;word-break:break-all}"
            + ".host code{background:#e7e7e9;padding:.1rem .3rem;border-radius:.2rem}"
            + "label{display:block;margin:1rem 0 .3rem}"
            + "input[type=password]{width:100%;padding:.6rem;font-size:1rem;box-sizing:border-box}"
            + "button{margin-top:1.2rem;padding:.6rem 1.2rem;font-size:1rem;cursor:pointer}"
            + ".err{color:#b00020;margin:.5rem 0}.scope{font-size:.9rem;color:#555}"
            + ".unverified{font-size:.85rem;color:#666;margin:.4rem 0 0}</style></head><body>");
        sb.Append("<h1>Authorize access to Landbridge</h1>");

        // The identity: the client_id host, then the whole client_id URL, then the
        // redirect_uri. client_id is the only field the fetcher proves (it must equal
        // the URL it came from), and the redirect_uri is where this grant physically
        // goes — together they are the human's real answer to "who is asking, and
        // where does the code land?" (CIMD draft §6.4/§6.7).
        sb.Append("<div class=\"client\"><b>")
          .Append(e.Encode(ClientIdHost(doc.ClientId) ?? "unidentified client")).Append("</b>");
        if (doc.ClientId is not null)
            sb.Append("<div class=\"host\">").Append(e.Encode(doc.ClientId)).Append("</div>");
        if (p.RedirectUri is not null)
            sb.Append("<div class=\"host\">Access will be sent to <code>")
              .Append(e.Encode(p.RedirectUri)).Append("</code></div>");

        // Unverified by construction: both are attacker-controllable document body,
        // so they are labelled rather than trusted — a document claiming the name and
        // site of a well-known client must not be able to borrow its reputation.
        sb.Append("<p class=\"unverified\">Claimed by the client, not verified: ")
          .Append(e.Encode(doc.ClientName ?? "(no name)"));
        if (doc.ClientUri is not null)
            sb.Append(" &lt;").Append(e.Encode(doc.ClientUri)).Append("&gt;");
        sb.Append("</p></div>");

        sb.Append("<p>This client is requesting a Landbridge human session");
        if (p.Scope is not null)
            sb.Append(" <span class=\"scope\">(scope: ").Append(e.Encode(p.Scope)).Append(")</span>");
        sb.Append(".</p>");

        if (error is not null)
            sb.Append("<p class=\"err\">").Append(e.Encode(error)).Append("</p>");

        sb.Append("<form method=\"post\" action=\"/oauth/authorize\">");
        // Re-post the exact request so the POST re-runs identical validation.
        AppendHidden(sb, e, "response_type", p.ResponseType);
        AppendHidden(sb, e, "client_id", p.ClientId);
        AppendHidden(sb, e, "redirect_uri", p.RedirectUri);
        AppendHidden(sb, e, "code_challenge", p.CodeChallenge);
        AppendHidden(sb, e, "code_challenge_method", p.CodeChallengeMethod);
        AppendHidden(sb, e, "state", p.State);
        AppendHidden(sb, e, "scope", p.Scope);
        AppendHidden(sb, e, "resource", p.Resource);
        sb.Append("<label for=\"passphrase\">Operator passphrase</label>");
        sb.Append("<input id=\"passphrase\" name=\"passphrase\" type=\"password\" autocomplete=\"current-password\" autofocus>");
        sb.Append("<button type=\"submit\">Authorize</button>");
        sb.Append("</form></body></html>");

        return Results.Content(sb.ToString(), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private static void AppendHidden(StringBuilder sb, HtmlEncoder e, string name, string? value)
    {
        if (value is null) return;
        sb.Append("<input type=\"hidden\" name=\"").Append(name)
          .Append("\" value=\"").Append(e.Encode(value)).Append("\">");
    }

    /// <summary>
    /// The hostname of the <c>client_id</c> URL, for the consent page's identity line.
    /// Deliberately never consults the document's <c>client_uri</c>: that field is
    /// unvalidated body text, so preferring it would let a malicious client render a
    /// trusted hostname above its own <c>redirect_uri</c>. Only <c>client_id</c> is
    /// pinned to an origin the client demonstrably controls (CIMD draft §4.1).
    /// </summary>
    private static string? ClientIdHost(string? clientId) =>
        clientId is not null && Uri.TryCreate(clientId, UriKind.Absolute, out var u) ? u.Host : null;

    private static string BuildErrorHtml(string error, string message)
    {
        var e = HtmlEncoder.Default;
        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Authorization error</title>"
            + "<style>body{font:16px system-ui,sans-serif;max-width:34rem;margin:3rem auto;padding:0 1rem}"
            + "code{background:#f4f4f5;padding:.1rem .3rem;border-radius:.2rem}</style></head><body>"
            + "<h1>Authorization error</h1><p><code>" + e.Encode(error) + "</code></p><p>"
            + e.Encode(message) + "</p></body></html>";
    }
}

/// <summary>RFC 6749 §5.1 successful token response (subset — no refresh token in v1).</summary>
internal sealed record TokenResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_type")] string TokenType,
    [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn,
    [property: System.Text.Json.Serialization.JsonPropertyName("scope")] string? Scope);

/// <summary>RFC 6749 §5.2 error response.</summary>
internal sealed record TokenErrorResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string Error,
    [property: System.Text.Json.Serialization.JsonPropertyName("error_description")] string ErrorDescription);
