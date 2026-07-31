namespace Docket.Preview;

/// <summary>
/// The HTTP preview frontend's options (config section <c>Preview</c>, spec §8.4).
/// </summary>
public sealed class PreviewOptions
{
    public const string SectionName = "Preview";

    /// <summary>
    /// The TCP port the frontend accepts browser connections on. Defaults to 443
    /// (HTTPS). A raw <c>TcpListener</c> owns it — the frontend byte-splices whole
    /// connections rather than parsing+re-emitting requests, so cookies and
    /// absolute paths in the served app are never rewritten (§8.4).
    /// </summary>
    public int ListenPort { get; set; } = 443;

    /// <summary>
    /// The wildcard origin's base domain — a request to <c>{label}.{Domain}</c> is
    /// routed to the preview <c>label</c> (§8.4). Routing is strictly by the Host
    /// header's leftmost label under this suffix; anything else is refused (404).
    /// </summary>
    public string Domain { get; set; } = "preview.localhost";

    /// <summary>
    /// Path to the wildcard-cert PEM (certificate chain) that terminates TLS on
    /// <c>*.{Domain}</c> (§8.4: a provided PEM to start, ACME DNS-01 later). When
    /// set alongside <see cref="CertKeyPemPath"/>, accepted connections are wrapped
    /// in TLS. When unset the frontend runs in <b>plaintext</b> — for local runs and
    /// for sitting behind a TLS-terminating load balancer that forwards HTTP/1.1.
    /// </summary>
    public string? CertPemPath { get; set; }

    /// <summary>Path to the wildcard cert's private-key PEM. Required when <see cref="CertPemPath"/> is set.</summary>
    public string? CertKeyPemPath { get; set; }

    /// <summary>
    /// How long to let cert-file writes settle before reloading (§8.4 PEM
    /// hot-reload). Renewal tools write the cert and key as two separate, non-atomic
    /// files and a single change emits a flurry of file-system events; debouncing
    /// coalesces that into one reload attempt. This is only a churn/latency knob —
    /// correctness does not depend on it, since a reload that catches a half-written
    /// or mismatched pair simply fails the load-both-and-match check and is retried
    /// on the next write.
    /// </summary>
    public TimeSpan CertReloadDebounce { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Base URL of the control plane the frontend calls <c>/preview/connect</c> on (§8.4).</summary>
    public string ControlPlaneUrl { get; set; } = "";

    /// <summary>The shared bearer the plane's <c>/preview/connect</c> endpoint requires (§8.4).</summary>
    public string? ControlPlaneBearer { get; set; }

    /// <summary>
    /// How long to wait for the browser to send its request head before giving up
    /// (a clean 408). A browser that connects and sends nothing must not pin a
    /// handler open.
    /// </summary>
    public TimeSpan HeadReadTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to wait for the <em>first upstream byte</em> after the tunnel is
    /// armed before surfacing a clean 504 (spec §8.4: failure modes return
    /// statuses, not hangs). A producer that is offline / never dials shows up as
    /// the relay closing the consumer end with no pair (→ 502) or as silence
    /// (→ 504) — never a hang. Kept below the relay's pair-wait so the browser gets
    /// a 504 rather than waiting out the relay.
    /// </summary>
    public TimeSpan FirstByteTimeout { get; set; } = TimeSpan.FromSeconds(20);
}
