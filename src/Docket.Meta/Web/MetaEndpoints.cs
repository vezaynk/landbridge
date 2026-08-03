using Docket.Meta.Auth;
using Docket.Meta.Data;
using Docket.Meta.Provisioning;
using Docket.Meta.Substrate;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Web;

/// <summary>
/// The meta panel's HTTP surface (design note §1). Server-rendered HTML only — no
/// MCP, no JSON agent API (spec §3: "human-only", "agent-facing anything on
/// docket-meta" is explicitly not built). Every page but the login door is gated by
/// the operator session; the door itself is fail-closed when no passphrase hash is
/// configured (mirrors the plane dashboard's operator seam).
/// </summary>
internal static class MetaEndpoints
{
    /// <summary>Fixed delay on a wrong passphrase — cheap brute-force friction (mirrors the plane).</summary>
    private static readonly TimeSpan WrongPassphraseDelay = TimeSpan.FromMilliseconds(400);

    public static WebApplication MapMeta(this WebApplication app)
    {
        // Open: the stylesheet and the login/logout seam.
        app.MapGet(MetaHtml.CssPath, () => Results.Content(MetaCss.Content, MetaCss.ContentType));
        app.MapGet("/login", (string? error) => Html(MetaRenderer.Login(error)));
        app.MapPost("/login", HandleLoginAsync);
        app.MapPost("/logout", (HttpContext http, MetaSessionStore sessions) =>
        {
            sessions.Revoke(http.Request.Cookies[MetaSessionStore.CookieName]);
            MetaAuth.ClearSessionCookie(http);
            return Results.Redirect("/login");
        });

        app.MapGet("/", () => Results.Redirect("/instances"));

        // Instances
        app.MapGet("/instances", GatedAsync(HandleInstancesAsync));
        app.MapGet("/instances/new", GatedAsync(HandleNewFormAsync));
        app.MapPost("/instances", GatedAsync(HandleCreateAsync));
        app.MapGet("/instances/{id:guid}", GatedAsync(HandleDetailAsync));
        app.MapPost("/instances/{id:guid}/suspend", GatedAsync(HandleActionAsync("suspend")));
        app.MapPost("/instances/{id:guid}/resume", GatedAsync(HandleActionAsync("resume")));
        app.MapPost("/instances/{id:guid}/retry", GatedAsync(HandleActionAsync("retry")));
        app.MapPost("/instances/{id:guid}/upgrade", GatedAsync(HandleUpgradeAsync));
        app.MapPost("/instances/{id:guid}/destroy", GatedAsync(HandleDestroyAsync));

        // Hosts
        app.MapGet("/hosts", GatedAsync(HandleHostsAsync));
        app.MapPost("/hosts", GatedAsync(HandleAddHostAsync));
        app.MapPost("/hosts/{id:guid}/remove", GatedAsync(HandleRemoveHostAsync));

        return app;
    }

    // ── Auth gate ──────────────────────────────────────────────────────────────

    /// <summary>Wraps a handler so it runs only for an authenticated operator; otherwise redirect to login.</summary>
    private static Func<HttpContext, Task<IResult>> GatedAsync(Func<HttpContext, Task<IResult>> handler) =>
        async http =>
        {
            var sessions = http.RequestServices.GetRequiredService<MetaSessionStore>();
            if (!MetaAuth.IsAuthenticated(http, sessions))
                return Results.Redirect("/login");
            return await handler(http);
        };

    private static async Task<IResult> HandleLoginAsync(
        HttpContext http, MetaOperatorVerifier verifier, MetaSessionStore sessions)
    {
        var form = await http.Request.ReadFormAsync();
        var passphrase = form["passphrase"].ToString();

        // Fail-closed when unconfigured — the panel can verify no one, so it mints nothing.
        if (!verifier.IsConfigured)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (!verifier.Verify(passphrase))
        {
            await Task.Delay(WrongPassphraseDelay);
            return Html(MetaRenderer.Login("Incorrect operator passphrase."), 401);
        }

        var (token, expiresAt) = sessions.Create();
        MetaAuth.SetSessionCookie(http, token, expiresAt);
        return Results.Redirect("/instances");
    }

    // ── Instances ────────────────────────────────────────────────────────────────

    private static async Task<IResult> HandleInstancesAsync(HttpContext http)
    {
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var tracker = http.RequestServices.GetRequiredService<OperationTracker>();
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();

        var instances = await db.Instances
            .Include(i => i.Host)
            .Where(i => i.State != InstanceState.Destroyed)
            .OrderBy(i => i.Name)
            .ToListAsync();

        var rows = instances
            .Select(i => (i, i.Host?.Name ?? "—", tracker.Busy(i.Id)))
            .ToList();
        return Html(MetaRenderer.InstancesList(rows, clock.GetUtcNow()));
    }

    private static async Task<IResult> HandleNewFormAsync(HttpContext http)
    {
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var options = http.RequestServices.GetRequiredService<MetaOptions>();
        var hosts = await db.Hosts.Where(h => h.RemovedAt == null).OrderBy(h => h.Name).ToListAsync();
        return Html(MetaRenderer.CreateForm(hosts, options.DefaultImageTag, error: null));
    }

    private static async Task<IResult> HandleCreateAsync(HttpContext http)
    {
        var creator = http.RequestServices.GetRequiredService<InstanceCreator>();
        var launcher = http.RequestServices.GetRequiredService<LifecycleLauncher>();
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var options = http.RequestServices.GetRequiredService<MetaOptions>();

        var form = await http.Request.ReadFormAsync();
        var req = new CreateInstanceRequest(
            Name: form["name"].ToString(),
            AccountLabel: form["account"].ToString(),
            ImageTag: form["tag"].ToString(),
            HostId: Guid.TryParse(form["host"].ToString(), out var h) ? h : null);

        try
        {
            var result = await creator.CreateAsync(req, http.RequestAborted);
            launcher.Provision(result.Id);
            return Html(MetaRenderer.Created(result));
        }
        catch (InstanceCreateException ex)
        {
            var hosts = await db.Hosts.Where(x => x.RemovedAt == null).OrderBy(x => x.Name).ToListAsync();
            return Html(MetaRenderer.CreateForm(hosts, options.DefaultImageTag, ex.Message, req.Name), 400);
        }
    }

    private static async Task<IResult> HandleDetailAsync(HttpContext http)
    {
        var id = http.GetRouteValue("id") is string s && Guid.TryParse(s, out var g) ? g : Guid.Empty;
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var tracker = http.RequestServices.GetRequiredService<OperationTracker>();
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();

        var instance = await db.Instances
            .Include(i => i.Host)
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (instance is null)
            return Results.NotFound();

        // Live health only for states where containers are expected up; wrap so a
        // Docker-unreachable host degrades to "no health shown", never a 500.
        InstanceHealth? health = null;
        if (instance.State is InstanceState.Ready or InstanceState.Suspended && instance.Host is not null)
        {
            try
            {
                var substrates = http.RequestServices.GetRequiredService<ISubstrateFactory>();
                var probe = http.RequestServices.GetRequiredService<InstanceHealthProbe>();
                health = await probe.CheckAsync(substrates.For(instance.Host), instance.Host, instance, http.RequestAborted);
            }
            catch { health = null; }
        }

        return Html(MetaRenderer.InstanceDetail(
            instance, instance.Host?.Name ?? "—", health, tracker.Busy(instance.Id), clock.GetUtcNow()));
    }

    private static Func<HttpContext, Task<IResult>> HandleActionAsync(string action) => async http =>
    {
        var id = RouteGuid(http);
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var launcher = http.RequestServices.GetRequiredService<LifecycleLauncher>();
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == id);
        if (instance is null)
            return Results.NotFound();

        switch (action)
        {
            case "suspend" when instance.State is InstanceState.Ready: launcher.Suspend(id); break;
            case "resume" when instance.State is InstanceState.Suspended: launcher.Resume(id); break;
            case "retry" when instance.State is InstanceState.Failed: launcher.Provision(id); break;
        }
        return Results.Redirect($"/instances/{id}");
    };

    private static async Task<IResult> HandleUpgradeAsync(HttpContext http)
    {
        var id = RouteGuid(http);
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var launcher = http.RequestServices.GetRequiredService<LifecycleLauncher>();
        var form = await http.Request.ReadFormAsync();
        var tag = form["tag"].ToString().Trim();

        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == id);
        if (instance is null)
            return Results.NotFound();
        if (!string.IsNullOrWhiteSpace(tag) && instance.State is InstanceState.Ready or InstanceState.Failed)
            launcher.Upgrade(id, tag);
        return Results.Redirect($"/instances/{id}");
    }

    private static async Task<IResult> HandleDestroyAsync(HttpContext http)
    {
        var id = RouteGuid(http);
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var launcher = http.RequestServices.GetRequiredService<LifecycleLauncher>();
        var form = await http.Request.ReadFormAsync();
        var confirm = form["confirm"].ToString().Trim();

        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == id);
        if (instance is null)
            return Results.NotFound();
        // Typed-name confirm: only destroy when the operator retyped the exact name.
        if (instance.State != InstanceState.Destroyed && confirm == instance.Name)
            launcher.Destroy(id);
        return Results.Redirect($"/instances/{id}");
    }

    // ── Hosts ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> HandleHostsAsync(HttpContext http)
    {
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var hosts = await db.Hosts.Where(h => h.RemovedAt == null).OrderBy(h => h.Name).ToListAsync();
        var counts = await db.Instances
            .Where(i => i.State != InstanceState.Destroyed)
            .GroupBy(i => i.HostId)
            .Select(gp => new { HostId = gp.Key, Count = gp.Count() })
            .ToDictionaryAsync(x => x.HostId, x => x.Count);

        var rows = hosts.Select(h => (h, counts.GetValueOrDefault(h.Id, 0))).ToList();
        return Html(MetaRenderer.Hosts(rows, error: null));
    }

    private static async Task<IResult> HandleAddHostAsync(HttpContext http)
    {
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();
        var form = await http.Request.ReadFormAsync();

        var name = form["name"].ToString().Trim();
        var endpoint = form["endpoint"].ToString().Trim();
        var published = form["published"].ToString().Trim();

        async Task<IResult> Reject(string message)
        {
            var hosts = await db.Hosts.Where(h => h.RemovedAt == null).OrderBy(h => h.Name).ToListAsync();
            var counts = await db.Instances.Where(i => i.State != InstanceState.Destroyed)
                .GroupBy(i => i.HostId).Select(gp => new { gp.Key, C = gp.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.C);
            var rows = hosts.Select(h => (h, counts.GetValueOrDefault(h.Id, 0))).ToList();
            return Html(MetaRenderer.Hosts(rows, message), 400);
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint))
            return await Reject("Name and endpoint are required.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return await Reject("Endpoint must be an absolute URI (unix:// or https://).");
        if (await db.Hosts.AnyAsync(h => h.Name == name && h.RemovedAt == null))
            return await Reject($"A host named '{name}' already exists.");

        var kind = uri.Scheme == "unix" ? HostEndpointKind.UnixSocket : HostEndpointKind.TcpMtls;
        if (kind == HostEndpointKind.TcpMtls &&
            (string.IsNullOrWhiteSpace(form["cert"]) || string.IsNullOrWhiteSpace(form["key"])))
            return await Reject("A remote (https) host needs a client cert and key PEM for mTLS.");

        db.Hosts.Add(new HostRow
        {
            Id = Guid.NewGuid(),
            Name = name,
            EndpointUri = endpoint,
            EndpointKind = kind,
            PublishedHost = string.IsNullOrWhiteSpace(published) ? "127.0.0.1" : published,
            TlsCaPem = NullIfBlank(form["ca"]),
            TlsClientCertPem = NullIfBlank(form["cert"]),
            TlsClientKeyPem = NullIfBlank(form["key"]),
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return Results.Redirect("/hosts");
    }

    private static async Task<IResult> HandleRemoveHostAsync(HttpContext http)
    {
        var id = RouteGuid(http);
        var db = http.RequestServices.GetRequiredService<MetaDbContext>();
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();

        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id && h.RemovedAt == null);
        if (host is null)
            return Results.Redirect("/hosts");

        var inUse = await db.Instances.AnyAsync(i => i.HostId == id && i.State != InstanceState.Destroyed);
        if (inUse)
            return Results.Redirect("/hosts"); // guarded: never orphan live instances
        host.RemovedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        return Results.Redirect("/hosts");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static Guid RouteGuid(HttpContext http) =>
        http.GetRouteValue("id") is string s && Guid.TryParse(s, out var g) ? g : Guid.Empty;

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static IResult Html(string html, int status = 200) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: status);
}
