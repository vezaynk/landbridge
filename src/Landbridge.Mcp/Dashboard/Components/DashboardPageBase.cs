using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace Landbridge.Mcp.Dashboard.Components;

/// <summary>
/// Shared load + refresh + principal resolution for gated dashboard pages.
/// Prerender paints a complete HTML document (tests and no-JS); a live circuit
/// then ticks <see cref="AutoRefresh"/> pages every 2s. Status codes are written
/// only while the HTTP response is still uncommitted.
/// </summary>
public abstract class DashboardPageBase : ComponentBase, IDisposable
{
    private readonly DashboardRefresh _refresh = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _reloading;
    private int _queued;
    private string? _token;
    private bool _listening;

    [Inject] protected TokenService Tokens { get; set; } = default!;
    [Inject] protected DashboardQueries Queries { get; set; } = default!;
    [Inject] protected TimeProvider Clock { get; set; } = default!;
    [Inject] protected IHttpContextAccessor Http { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] IJSRuntime JS { get; set; } = default!;
    [Inject] DashboardWindowState WindowState { get; set; } = default!;

    protected Principal? Principal { get; private set; }
    protected TimeSpan WindowDuration { get; private set; } = DashboardWindow.Default.Duration;
    protected string WindowLabel { get; private set; } = DashboardWindow.Default.Label;
    protected DateTimeOffset Now { get; private set; }
    protected string? RefuseReason { get; private set; }
    protected bool Ready { get; private set; }
    protected int StatusOnRefuse { get; set; } = StatusCodes.Status403Forbidden;
    protected virtual bool AutoRefresh => true;

    /// <summary>
    /// Circuit lifetime, not the prerender HTTP request. Using
    /// <c>HttpContext.RequestAborted</c> after the first paint cancels every
    /// refresh — the GET is already finished.
    /// </summary>
    protected CancellationToken RequestAborted => _lifetime.Token;

    /// <summary>Null is the instance-wide human view; a list is a Lead's owned Teams.</summary>
    protected IReadOnlyList<Guid>? TeamScope { get; private set; }

    /// <summary>Dashboard aliases for <see cref="TeamScope"/>, empty for a human.</summary>
    protected IReadOnlyDictionary<Guid, string> TeamSlugs { get; private set; } =
        new Dictionary<Guid, string>();

    protected bool OperatorMayAccess(TeamId team) => Principal switch
    {
        Principal.Human => true,
        Principal.Lead => TeamScope is not null && TeamScope.Contains(team.Value),
        _ => false,
    };

    protected override void OnInitialized()
    {
        if (_listening)
            return;
        Nav.LocationChanged += OnLocationChanged;
        _listening = true;
    }

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e) =>
        _ = InvokeAsync(ReloadAsync);

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender || !AutoRefresh || RefuseReason is not null)
            return;
        _refresh.Start(() => InvokeAsync(ReloadAsync));
    }

    protected async Task ReloadAsync()
    {
        if (Interlocked.Exchange(ref _reloading, 1) == 1)
        {
            Interlocked.Exchange(ref _queued, 1);
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _queued, 0);
                try
                {
                    await ReloadCoreAsync();
                }
                catch (OperationCanceledException) { }
                catch (NavigationException) { throw; }
                catch (Exception)
                {
                    // Refresh is opportunistic. A failed read (including Npgsql
                    // overlapping the previous command) must not crash the circuit.
                }
            }
            while (Interlocked.Exchange(ref _queued, 0) == 1);
        }
        finally
        {
            Interlocked.Exchange(ref _reloading, 0);
            if (Interlocked.Exchange(ref _queued, 0) == 1)
                await ReloadAsync();
        }
    }

    private async Task ReloadCoreAsync()
    {
        var http = Http.HttpContext;
        _token ??= http is null ? null : DashboardAuth.ReadToken(http);
        if (string.IsNullOrWhiteSpace(_token))
        {
            Nav.NavigateTo("/dashboard/login", replace: true);
            return;
        }

        Principal = await Tokens.ValidateAsync(_token, RequestAborted) switch
        {
            Principal.Human h => h,
            Principal.Lead l => l,
            _ => null,
        };
        if (Principal is null)
        {
            Nav.NavigateTo("/dashboard/login", replace: true);
            return;
        }

        if (Principal is Principal.Lead lead)
        {
            var slugs = await Tokens.OwnedTeamSlugsAsync(lead.CredentialId, RequestAborted);
            TeamSlugs = slugs;
            TeamScope = slugs.Keys.ToList();
        }
        else if (Principal is Principal.Human)
        {
            TeamSlugs = new Dictionary<Guid, string>();
            TeamScope = null;
        }
        else
        {
            TeamSlugs = new Dictionary<Guid, string>();
            TeamScope = [];
        }

        Now = Clock.GetUtcNow();
        await ApplyWindowAsync(http);
        StatusOnRefuse = StatusCodes.Status403Forbidden;
        RefuseReason = await LoadAsync();
        if (RefuseReason is not null && http is { Response.HasStarted: false })
            http.Response.StatusCode = StatusOnRefuse;
        Ready = true;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Returns a refusal reason or null on success. Set
    /// <see cref="StatusOnRefuse"/> before returning to pick 400/403/404.</summary>
    protected abstract Task<string?> LoadAsync();

    /// <summary>
    /// Enhanced navigation updates <see cref="NavigationManager.Uri"/> without a
    /// new HTTP request, so the lookback is read from the live URI first. The
    /// circuit-scoped choice wins over the original request's cookie after that.
    /// </summary>
    private async Task ApplyWindowAsync(HttpContext? http)
    {
        var query = DashboardWindow.QueryValue(Nav.Uri);
        if (DashboardWindow.TryParse(query, out var fromQuery))
        {
            WindowState.Set(fromQuery);
            await PersistPrefCookieAsync(DashboardWindow.CookieName, fromQuery.Key, http);
        }
        else if (!WindowState.Chosen && http is not null)
            WindowState.Set(DashboardWindow.Resolve(http));
        WindowDuration = WindowState.Current.Duration;
        WindowLabel = WindowState.Current.Label;
    }

    protected async Task PersistPrefCookieAsync(string name, string key, HttpContext? http = null)
    {
        http ??= Http.HttpContext;
        if (http is { Response.HasStarted: false })
        {
            if (name == DashboardWindow.CookieName)
                DashboardWindow.WriteCookie(http, key);
            else if (name == DashboardTeam.CookieName)
                DashboardTeam.WriteCookie(http, key);
            return;
        }
        try
        {
            var secure = http?.Request.IsHttps
                ?? Nav.Uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
            await JS.InvokeVoidAsync("landbridgeSetPref", name, key, secure);
        }
        catch (InvalidOperationException) { }
        catch (JSDisconnectedException) { }
    }

    public void Dispose()
    {
        if (_listening)
        {
            Nav.LocationChanged -= OnLocationChanged;
            _listening = false;
        }
        _refresh.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
