using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// <c>report_friction</c> for both Leads and workers: persist, refuse empty/over-cap,
/// refuse anything that is not a live Lead or dispatched worker.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FrictionToolsTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private readonly FakeTimeProvider _clock = new();

    private static IHttpContextAccessor AccessorFor(Principal principal) =>
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = LandbridgeClaims.ToClaimsPrincipal(principal) } };

    private FrictionTools ToolsFor(Principal principal) =>
        new(new FrictionStore(pg.NewContext(), _clock), AccessorFor(principal));

    [SkippableFact]
    public async Task A_lead_and_a_worker_can_each_record_friction()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var session = SessionId.New();
        var leadHuman = Guid.NewGuid();

        var leadAck = await ToolsFor(new Principal.Lead(Team, leadHuman))
            .ReportFriction("list_profiles hid a saturated machine as missing", CancellationToken.None);
        Assert.Equal("ok: friction recorded", leadAck);

        var workerAck = await ToolsFor(new Principal.Worker(new WorkerCaller(Team, session, WorkerInstanceId.New())))
            .ReportFriction("start_process refused with no name in the reason", CancellationToken.None);
        Assert.Equal("ok: friction recorded", workerAck);

        await using var db = pg.NewContext();
        var rows = await db.FrictionReports.AsNoTracking().OrderBy(r => r.Seq).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(FrictionReportRow.LeadRole, rows[0].Role);
        Assert.Equal(Team.Value, rows[0].TeamId);
        Assert.Null(rows[0].SessionId);
        Assert.Equal(leadHuman, rows[0].HumanId);
        Assert.Contains("list_profiles", rows[0].Message, StringComparison.Ordinal);
        Assert.Equal(FrictionReportRow.WorkerRole, rows[1].Role);
        Assert.Equal(session.Value, rows[1].SessionId);
        Assert.Null(rows[1].HumanId);
    }

    [SkippableFact]
    public async Task Empty_and_over_cap_messages_are_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = ToolsFor(new Principal.Lead(Team));

        var empty = await Assert.ThrowsAsync<McpException>(() => tools.ReportFriction("   ", CancellationToken.None));
        Assert.Contains("required", empty.Message, StringComparison.OrdinalIgnoreCase);

        var over = new string('x', FrictionStore.MaxMessageBytes + 1);
        var cap = await Assert.ThrowsAsync<McpException>(() => tools.ReportFriction(over, CancellationToken.None));
        Assert.Contains("cap", cap.Message, StringComparison.OrdinalIgnoreCase);

        await using var db = pg.NewContext();
        Assert.Empty(await db.FrictionReports.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task A_human_session_cannot_report_friction()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = ToolsFor(new Principal.Human(Guid.NewGuid()));
        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.ReportFriction("this should not land", CancellationToken.None));
        Assert.Contains("lead claim or a dispatched worker", ex.Message, StringComparison.Ordinal);
    }
}
