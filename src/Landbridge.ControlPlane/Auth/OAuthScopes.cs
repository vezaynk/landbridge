namespace Landbridge.ControlPlane.Auth;

/// <summary>The coarse OAuth scope vocabulary for v1 (§5).</summary>
public static class OAuthScopes
{
    /// <summary>
    /// A single coarse scope. Landbridge's §5 authority model is structural
    /// (human → lead → worker), not scope-graded, so a completed flow mints the
    /// full human session regardless of requested scope; granular OAuth scopes
    /// are a documented follow-up. Advertised so a client has a concrete value to
    /// request.
    ///
    /// <para>Shared: the resource server advertises it in its protected-resource
    /// document and the authorization server in its own, and the two must agree.</para>
    /// </summary>
    public const string Landbridge = "landbridge";
}
