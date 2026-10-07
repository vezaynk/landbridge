using Landbridge.Core;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PlanStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private static LeadClaim Lead => new(Team);

    private static SessionStore Store(LandbridgeDbContext db) => new(db, TimeProvider.System);

    private static MachineSnapshot Machine() =>
        new(TestMachineIds.For("m1"), Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    private async Task<(SessionId Id, WorkerCaller Caller)> Working(LandbridgeDbContext db)
    {
        var created = Assert.IsType<StoreResult.Applied>(
            await Store(db).CreateAsync(new CreateSession(Lead, Team, "fix the build", "default")));
        var instance = WorkerInstanceId.New();
        await Store(db).DispatchNextAsync(Machine(), instance);
        var caller = new WorkerCaller(Team, created.Session.Id, instance);
        return (created.Session.Id, caller);
    }

    [SkippableFact]
    public async Task Approve_stores_the_plan_and_a_waiting_revision_does_not_replace_it()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (id, caller) = await Working(db);

        Assert.IsType<StoreResult.Applied>(await Store(db).SubmitPlanAsync(caller, "run the tests"));
        var waiting = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id.Value);
        Assert.Equal(MessageState.AwaitingPlan, waiting.MessageState);
        Assert.Equal(InputRequestKind.Plan, waiting.InputKind);
        Assert.Equal("run the tests", waiting.InputQuestion);
        Assert.Null(waiting.ApprovedPlan);
        Assert.Null(waiting.PlanVerdict);

        var inbox = Assert.Single(
            (await Store(db).GetLeadInboxAsync(Team, [id.Value], actor: Lead)).Items,
            i => i.Kind == LeadInboxKind.Plan);
        Assert.Equal("run the tests", inbox.Question);

        Assert.IsType<StoreResult.Rejected>(
            await Store(db).AnswerPlanAsync(Lead, id, PlanVerdict.Deny));
        Assert.IsType<StoreResult.Applied>(
            await Store(db).AnswerPlanAsync(Lead, id, PlanVerdict.Approve, "ship it"));

        var approved = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id.Value);
        Assert.Equal("run the tests", approved.ApprovedPlan);
        Assert.Equal(PlanVerdict.Approve, approved.PlanVerdict);
        Assert.Equal(MessageState.Idle, approved.MessageState);
        Assert.Equal(caller.Instance.Value, approved.CurrentInstanceId);
        Assert.Equal("run the tests", await Store(db).GetApprovedPlanAsync(id));

        Assert.IsType<StoreResult.Applied>(await Store(db).SubmitPlanAsync(caller, "also format the files"));
        var revising = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id.Value);
        Assert.Equal(MessageState.AwaitingPlan, revising.MessageState);
        Assert.Equal("also format the files", revising.InputQuestion);
        Assert.Equal("run the tests", revising.ApprovedPlan);
        Assert.Null(revising.PlanVerdict);

        Assert.IsType<StoreResult.Rejected>(
            await Store(db).SubmitPlanAsync(caller, "a third plan"));
        Assert.IsType<StoreResult.Rejected>(
            await Store(db).SendInputResponseAsync(Lead, id, Machine().MachineId, "just do it"));

        Assert.IsType<StoreResult.Applied>(
            await Store(db).AnswerPlanAsync(Lead, id, PlanVerdict.Deny, "leave the formatting"));
        var denied = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id.Value);
        Assert.Equal("run the tests", denied.ApprovedPlan);
        Assert.Equal(PlanVerdict.Deny, denied.PlanVerdict);
        Assert.Equal(MessageState.Idle, denied.MessageState);
    }

    [SkippableFact]
    public async Task The_worker_wait_returns_the_leads_decision()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var open = pg.NewContext();
        var (id, caller) = await Working(open);
        Assert.IsType<StoreResult.Applied>(await Store(open).SubmitPlanAsync(caller, "run the tests"));

        await using var waitingDb = pg.NewContext();
        var pending = Store(waitingDb).AwaitPlanVerdictAsync(
            caller, TimeSpan.FromMilliseconds(20), TimeProvider.System, CancellationToken.None);

        await using var answer = pg.NewContext();
        Assert.IsType<StoreResult.Applied>(
            await Store(answer).AnswerPlanAsync(Lead, id, PlanVerdict.Deny, "narrow it"));

        var outcome = await pending;
        Assert.NotNull(outcome);
        Assert.Equal(PlanVerdict.Deny, outcome.Verdict);
        Assert.Equal("narrow it", outcome.Message);
    }
}
