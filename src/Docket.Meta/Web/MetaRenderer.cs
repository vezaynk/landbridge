using System.Text;
using Docket.Meta.Data;
using Docket.Meta.Provisioning;
using static Docket.Meta.Web.MetaHtml;

namespace Docket.Meta.Web;

/// <summary>Server-rendered page bodies for the meta panel (design note §1). Pure functions of their inputs.</summary>
internal static class MetaRenderer
{
    // ── Login ────────────────────────────────────────────────────────────────
    public static string Login(string? error)
    {
        var err = error is null ? "" : $"<p class=\"err\">{E(error)}</p>";
        return Page("Sign in", "", $"""
            <div class="login-wrap">
              <h1>Docket Meta</h1>
              <p class="sub">Operator control panel</p>
              {err}
              <form method="post" action="/login">
                <label for="passphrase">Operator passphrase</label>
                <input id="passphrase" name="passphrase" type="password" autofocus autocomplete="current-password">
                <button type="submit">Sign in</button>
              </form>
            </div>
            """, autoRefresh: false);
    }

    // ── Instances list ─────────────────────────────────────────────────────────
    public static string InstancesList(
        IReadOnlyList<(InstanceRow Instance, string HostName, string? Busy)> rows, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Instances</h1><p class=\"sub\">Provisioned control-plane deployments.</p>");
        sb.Append("<div class=\"actions\"><a class=\"btn primary\" href=\"/instances/new\">Create instance</a></div>");

        if (rows.Count == 0)
        {
            sb.Append(Empty("No instances yet."));
            return Page("Instances", "instances", sb.ToString());
        }

        sb.Append("<section><table><thead><tr>" +
                  "<th>Name</th><th>Account</th><th>Host</th><th>Image</th><th>State</th><th>Created</th>" +
                  "</tr></thead><tbody>");
        foreach (var (i, hostName, busy) in rows)
        {
            sb.Append("<tr>");
            sb.Append($"<td><a href=\"/instances/{i.Id}\">{E(i.Name)}</a></td>");
            sb.Append($"<td>{E(i.AccountLabel ?? "—")}</td>");
            sb.Append($"<td>{E(hostName)}</td>");
            sb.Append($"<td><code>{E(i.ImageTag)}</code></td>");
            sb.Append($"<td>{StateBadge(i.State, busy)}</td>");
            sb.Append($"<td>{E(Age(i.CreatedAt, now))}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></section>");
        return Page("Instances", "instances", sb.ToString());
    }

    // ── Create form ──────────────────────────────────────────────────────────
    public static string CreateForm(IReadOnlyList<HostRow> hosts, string defaultTag, string? error, string nameValue = "")
    {
        var err = error is null ? "" : $"<p class=\"err\">{E(error)}</p>";
        var hostOptions = new StringBuilder("<option value=\"\">Auto (least loaded)</option>");
        foreach (var h in hosts)
            hostOptions.Append($"<option value=\"{h.Id}\">{E(h.Name)}</option>");

        return Page("Create instance", "instances", $"""
            <h1>Create instance</h1>
            <p class="sub">Provisions a network, Postgres, docket-mcp, and docket-relay on the chosen host.</p>
            {err}
            <section>
              <form method="post" action="/instances">
                <label for="name">Name</label>
                <input id="name" name="name" type="text" value="{E(nameValue)}" autofocus placeholder="acme">
                <p class="hint">A DNS label — becomes <code>&lt;name&gt;.docket.&lt;domain&gt;</code>.</p>
                <label for="account">Account label (optional)</label>
                <input id="account" name="account" type="text" placeholder="Acme Corp">
                <label for="tag">Image tag</label>
                <input id="tag" name="tag" type="text" value="{E(defaultTag)}" placeholder="sha-1a2b3c4d5e6f">
                <p class="hint">A published tag — prefer an immutable <code>sha-&lt;commit&gt;</code> one.
                  Nothing publishes <code>latest</code>.</p>
                <label for="host">Host</label>
                <select id="host" name="host">{hostOptions}</select>
                <div class="actions"><button class="btn primary" type="submit">Create</button>
                  <a class="btn" href="/instances">Cancel</a></div>
              </form>
            </section>
            """, autoRefresh: false);
    }

    // ── Shown-once passphrase page ─────────────────────────────────────────────
    public static string Created(CreateInstanceResult result)
    {
        return Page("Instance created", "instances", $"""
            <h1>Instance '{E(result.Name)}' is provisioning</h1>
            <p class="sub">Provisioning runs in the background. Watch progress on the instance page.</p>
            <section>
              <h2>Operator passphrase — shown once</h2>
              <p>This is the initial operator passphrase for the new instance. It is
                 <strong>not stored</strong> and cannot be shown again. Copy it now and hand it to
                 the instance operator; only its hash was injected into the instance.</p>
              <code class="secret">{E(result.Passphrase)}</code>
              <div class="actions"><a class="btn primary" href="/instances/{result.Id}">Go to instance</a></div>
            </section>
            """, autoRefresh: false);
    }

    // ── Instance detail ────────────────────────────────────────────────────────
    public static string InstanceDetail(
        InstanceRow i, string hostName, InstanceHealth? health, string? busy, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append($"<h1>{E(i.Name)} {StateBadge(i.State, busy)}</h1>");
        sb.Append($"<p class=\"sub\">{E(i.PublicUrl ?? "(no public URL yet)")}</p>");

        // Facts
        sb.Append("<section><dl class=\"kv\">");
        Kv(sb, "Account", i.AccountLabel ?? "—");
        Kv(sb, "Host", hostName);
        Kv(sb, "Image tag", i.ImageTag);
        Kv(sb, "MCP port", i.McpPublishedPort?.ToString() ?? "—");
        Kv(sb, "Relay port", i.RelayPublishedPort?.ToString() ?? "—");
        Kv(sb, "Created", Age(i.CreatedAt, now));
        if (i.State == InstanceState.Failed && i.FailedStep is { } fs)
            Kv(sb, "Failed step", fs.ToString());
        sb.Append("</dl>");

        if (health is not null)
        {
            string Dot(bool ok) => ok ? "<span class=\"ok\">up</span>" : "<span class=\"down\">down</span>";
            sb.Append("<dl class=\"kv\">");
            sb.Append($"<dt>Postgres</dt><dd>{Dot(health.PgRunning)}</dd>");
            sb.Append($"<dt>Plane (MCP)</dt><dd>{Dot(health.McpResponding)}</dd>");
            sb.Append($"<dt>Relay</dt><dd>{Dot(health.RelayRunning)}</dd>");
            sb.Append("</dl>");
        }
        sb.Append("</section>");

        // Actions (guarded by state; a destroyed instance offers nothing)
        if (i.State != InstanceState.Destroyed)
        {
            sb.Append("<section><h2>Actions</h2><div class=\"actions\">");
            if (i.State is InstanceState.Ready)
                sb.Append(PostButton($"/instances/{i.Id}/suspend", "Suspend", "btn"));
            if (i.State is InstanceState.Suspended)
                sb.Append(PostButton($"/instances/{i.Id}/resume", "Resume", "btn"));
            if (i.State is InstanceState.Failed)
                sb.Append(PostButton($"/instances/{i.Id}/retry", "Retry provisioning", "btn"));
            sb.Append("</div>");

            // Upgrade
            sb.Append($"""
                <form method="post" action="/instances/{i.Id}/upgrade" class="actions" style="margin-top:12px">
                  <input type="text" name="tag" value="{E(i.ImageTag)}" style="max-width:200px">
                  <button class="btn" type="submit">Upgrade image</button>
                </form>
                """);

            // Destroy — typed-name confirm
            sb.Append($"""
                <form method="post" action="/instances/{i.Id}/destroy" class="actions" style="margin-top:12px"
                      onsubmit="return confirm('Destroy {E(i.Name)}? This removes its containers, network, and VOLUME (data is lost).')">
                  <input type="text" name="confirm" placeholder="type the instance name to confirm" style="max-width:280px">
                  <button class="btn danger" type="submit">Destroy</button>
                </form>
                """);
            sb.Append("</section>");
        }

        // Saga event log
        sb.Append("<section><h2>Provisioning steps</h2>");
        if (i.Steps.Count == 0)
            sb.Append(Empty("No steps recorded yet."));
        else
        {
            sb.Append("<table><thead><tr><th>Step</th><th>Status</th><th>Detail</th><th>Updated</th></tr></thead><tbody>");
            foreach (var s in i.Steps.OrderBy(s => s.Step))
            {
                var cls = s.Status switch
                {
                    StepStatus.Done => "step-done",
                    StepStatus.Failed => "step-failed",
                    _ => "step-pending",
                };
                var detail = s.Status == StepStatus.Failed ? s.Error : s.ExternalRef;
                sb.Append($"<tr><td>{E(s.Step.ToString())}</td>" +
                          $"<td class=\"{cls}\">{E(s.Status.ToString())}</td>" +
                          $"<td><code>{E(Truncate(detail, 80))}</code></td>" +
                          $"<td>{E(Age(s.UpdatedAt == default ? null : s.UpdatedAt, now))}</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        sb.Append("<div class=\"actions\"><a class=\"btn\" href=\"/instances\">Back to instances</a></div>");
        return Page(i.Name, "instances", sb.ToString());
    }

    // ── Hosts ──────────────────────────────────────────────────────────────────
    public static string Hosts(IReadOnlyList<(HostRow Host, int InstanceCount)> rows, string? error)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Hosts</h1><p class=\"sub\">The Docker host pool instances are placed on.</p>");
        if (error is not null)
            sb.Append($"<p class=\"err\">{E(error)}</p>");

        if (rows.Count == 0)
            sb.Append(Empty("No hosts registered. Add one below."));
        else
        {
            sb.Append("<section><table><thead><tr>" +
                      "<th>Name</th><th>Endpoint</th><th>Kind</th><th>Instances</th><th>Ports</th><th></th>" +
                      "</tr></thead><tbody>");
            foreach (var (h, count) in rows)
            {
                sb.Append("<tr>");
                sb.Append($"<td>{E(h.Name)}</td>");
                sb.Append($"<td><code>{E(h.EndpointUri)}</code></td>");
                sb.Append($"<td>{E(h.EndpointKind.ToString())}</td>");
                sb.Append($"<td>{count}</td>");
                sb.Append($"<td><code>{h.PortRangeLow}-{h.PortRangeHigh}</code></td>");
                sb.Append(count == 0
                    ? $"<td>{PostButton($"/hosts/{h.Id}/remove", "Remove", "btn danger")}</td>"
                    : "<td><span class=\"hint\">in use</span></td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table></section>");
        }

        sb.Append("""
            <section>
              <h2>Add host</h2>
              <form method="post" action="/hosts">
                <label for="hname">Name</label>
                <input id="hname" name="name" type="text" placeholder="local">
                <label for="endpoint">Docker Engine endpoint</label>
                <input id="endpoint" name="endpoint" type="text" value="unix:///var/run/docker.sock">
                <p class="hint">A unix socket for the local pool-of-one, or <code>https://host:2376</code> for a remote host.</p>
                <label for="published">Published host (edge upstream address)</label>
                <input id="published" name="published" type="text" value="127.0.0.1">
                <label for="ca">TLS CA PEM (remote only)</label>
                <input id="ca" name="ca" type="text" placeholder="-----BEGIN CERTIFICATE----- …">
                <label for="cert">TLS client cert PEM (remote only)</label>
                <input id="cert" name="cert" type="text">
                <label for="key">TLS client key PEM (remote only)</label>
                <input id="key" name="key" type="text">
                <div class="actions"><button class="btn primary" type="submit">Add host</button></div>
              </form>
            </section>
            """);
        return Page("Hosts", "hosts", sb.ToString());
    }

    private static void Kv(StringBuilder sb, string k, string v) => sb.Append($"<dt>{E(k)}</dt><dd>{E(v)}</dd>");

    private static string PostButton(string action, string label, string cls) =>
        $"<form method=\"post\" action=\"{action}\"><button class=\"{cls}\" type=\"submit\">{E(label)}</button></form>";

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "—" : s.Length <= max ? s : s[..max] + "…";
}
