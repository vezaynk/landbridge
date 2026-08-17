using System.Runtime.CompilerServices;

// §12 dashboard: the cookie-auth plumbing (DashboardAuth) and the HTML kit are
// internal — only DashboardEndpoints.MapDashboard is public surface. The dashboard
// end-to-end test asserts against the session-cookie name and drives the same
// helpers, so it needs internal visibility. No InternalsVisibleTo existed for
// Landbridge.Mcp before this change; the file lives under Dashboard/ because that is
// the change's fence.
[assembly: InternalsVisibleTo("Landbridge.Mcp.Tests")]
