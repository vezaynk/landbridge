using System.Text.RegularExpressions;

namespace Docket.Verifier;

/// <summary>
/// The verifier's verdict decision (spec §7) — its <b>own</b> policy, applied to
/// the opaque <c>resultReference</c> the worker reported. The plane never
/// interprets task content; interpretation lives here, in the verifier the
/// operator provisioned.
///
/// <para>A verdict is <c>accept</c> iff the reference is present <b>and</b>
/// matches the configured pattern; anything else is <c>fail</c>. The emptiness
/// check is deliberately separate from the pattern: the default pattern
/// <c>.*</c> matches the empty string, so without the guard a missing reference
/// would wrongly accept. A null or blank reference therefore always fails — which
/// also mirrors the plane's own invariant that a task cannot reach
/// <c>verifying</c> without a reference (§6), so on the wire this case is a
/// belt-and-suspenders guard, not an expected input.</para>
/// </summary>
public sealed class VerdictPolicy(Regex acceptPattern)
{
    /// <summary>True when the reference is non-blank and matches the accept pattern.</summary>
    public bool Accepts(string? resultReference) =>
        !string.IsNullOrWhiteSpace(resultReference) && acceptPattern.IsMatch(resultReference);

    /// <summary>The verdict token the webhook expects (§10): <c>"accept"</c> or <c>"fail"</c>.</summary>
    public string Decide(string? resultReference) => Accepts(resultReference) ? "accept" : "fail";
}
