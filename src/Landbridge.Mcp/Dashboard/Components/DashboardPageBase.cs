using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.AspNetCore.Components;

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
    private string? _token;

    [Inject] protected TokenService Tokens { get; set; } = default!;
    [Inject] protected DashboardQueries Queries { get; set; } = default!;
    [Inject] protected TimeProvider Clock { get; set; } = default!;
    [Inject] protected IHttpContextAccessor Http { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected Principal? Principal { get; private set; }
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

    protected bool OperatorMayAccess(TeamId team) => Principal switch
    {
        Principal.Human => true,
        Principal.Lead => TeamScope is not null && TeamScope.Contains(team.Value),
        _ => false,
    };

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender || !AutoRefresh || RefuseReason is not null)
            return;
        _refresh.Start(() => InvokeAsync(ReloadAsync));
    }

    protected async Task ReloadAsync()
    {
        if (Interlocked.Exchange(ref _reloading, 1) == 1)
            return;
        try
        {
            await ReloadCoreAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Exchange(ref _reloading, 0);
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

        TeamScope = Principal switch
        {
            Principal.Human => null,
            Principal.Lead l => await Tokens.OwnedTeamIdsAsync(l.CredentialId, RequestAborted),
            _ => [],
        };

        Now = Clock.GetUtcNow();
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

    public void Dispose()
    {
        _refresh.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
