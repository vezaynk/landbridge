using Landbridge.ControlPlane;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane.Tests;

[Collection(PostgresCollection.Name)]
public sealed class LeadInboxTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private static LeadClaim Lead => new(Team);

    private static SessionStore NewStore(LandbridgeDbContext db) => new(db, TimeProvider.System);

    private static MachineSnapshot Machine() =>
        new("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    [SkippableFact]
    public async Task A_fresh_team_has_an_empty_inbox()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var inbox = await NewStore(db).GetLeadInboxAsync(Team);
        Assert.Empty(inbox.Items);
    }

    [SkippableFact]
    public async Task A_question_is_kind_question_with_the_envelope_id()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (id, instance, store) = await SeedWorkingAsync(db);
        var asked = Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            id, new RequestInput(new WorkerCaller(Team, id, instance),
                InputRequestKind.Question, "which DB?")));

        var item = Assert.Single((await NewStore(db).GetLeadInboxAsync(Team)).Items);
        Assert.True(HaikuSlug.IsWellFormed(item.SessionId));
        Assert.Equal(LeadInboxKind.Question, item.Kind);
        Assert.Equal(asked.Session.MessageId, item.MessageId);
        Assert.Equal($"team-{Team}/session-{id}", item.Namespace);
    }

    [SkippableFact]
    public async Task Permission_and_report_are_distinct_kinds()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var perm = await SeedWorkingAsync(db, "perm");
        Assert.IsType<StoreResult.Applied>(await perm.Store.ApplyAsync(
            perm.Id, new RequestInput(new WorkerCaller(Team, perm.Id, perm.Instance),
                InputRequestKind.Permission, "{}", "Bash")));

        var report = await SeedWorkingAsync(db, "report");
        Assert.IsType<StoreResult.Applied>(await report.Store.ApplyAsync(
            report.Id, new ReportResult(new WorkerCaller(Team, report.Id, report.Instance), "git:ref")));

        var items = (await NewStore(db).GetLeadInboxAsync(Team)).Items;
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Kind == LeadInboxKind.Permission);
        Assert.Contains(items, i => i.Kind == LeadInboxKind.Report);
    }

    [SkippableFact]
    public async Task Failed_and_a_leftover_envelope_are_both_listed()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (id, instance, store) = await SeedWorkingAsync(db);
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            id, new RequestInput(new WorkerCaller(Team, id, instance),
                InputRequestKind.Question, "still waiting?")));
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            id, new LivenessLost(LivenessLossReason.ProcessExited)));

        var items = (await NewStore(db).GetLeadInboxAsync(Team)).Items;
        Assert.Equal(2, items.Count);
        Assert.Equal(LeadInboxKind.Failed, items[0].Kind);
        Assert.Equal(LeadInboxKind.Question, items[1].Kind);
        Assert.Equal(items[0].SessionId, items[1].SessionId);
        Assert.True(HaikuSlug.IsWellFormed(items[0].SessionId));
        Assert.NotNull(items[0].MessageId);
        Assert.Equal(items[0].MessageId, items[1].MessageId);
    }

    [SkippableFact]
    public async Task Hidden_rows_are_omitted_and_awaiting_pull_is_listed()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        var hidden = await SeedWorkingAsync(db, "hide");
        Assert.IsType<StoreResult.Applied>(await hidden.Store.ApplyAsync(
            hidden.Id, new ReportResult(new WorkerCaller(Team, hidden.Id, hidden.Instance), "git:ref")));
        Assert.IsType<StoreResult.Applied>(await hidden.Store.ApplyAsync(
            hidden.Id, new VerdictAccept(Lead)));

        var pull = await SeedWorkingAsync(db, "pull");
        Assert.IsType<StoreResult.Applied>(await pull.Store.ApplyAsync(
            pull.Id, new RequestInput(new WorkerCaller(Team, pull.Id, pull.Instance),
                InputRequestKind.Question, "before answer")));
        Assert.IsType<StoreResult.Applied>(await pull.Store.AnswerOrWakeAsync(
            Lead, pull.Id, "m1", "use postgres", sessionLive: true));

        var item = Assert.Single((await NewStore(db).GetLeadInboxAsync(Team)).Items);
        Assert.True(HaikuSlug.IsWellFormed(item.SessionId));
        Assert.Equal(LeadInboxKind.Pull, item.Kind);
    }

    [SkippableFact]
    public async Task A_filtered_read_with_a_lead_actor_delivers_unread_report_mail()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var report = await SeedWorkingAsync(db, "report");
        Assert.IsType<StoreResult.Applied>(await report.Store.ApplyAsync(
            report.Id, new ReportResult(new WorkerCaller(Team, report.Id, report.Instance), "git:ref", "done")));

        var flagged = Assert.Single((await NewStore(db).GetLeadInboxAsync(Team)).Items);
        Assert.Equal(LeadInboxKind.Report, flagged.Kind);
        Assert.Null(flagged.ResultReference);

        var delivered = await NewStore(db).GetLeadInboxAsync(Team, [report.Id.Value], actor: Lead);
        var item = Assert.Single(delivered.Items);
        Assert.Equal("git:ref", item.ResultReference);
        Assert.Equal("done", item.Report);

        Assert.Empty((await NewStore(db).GetLeadInboxAsync(Team)).Items);
    }

    [SkippableFact]
    public async Task Session_filter_hides_other_sessions_in_the_same_team()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var a = await SeedWorkingAsync(db, "a");
        Assert.IsType<StoreResult.Applied>(await a.Store.ApplyAsync(
            a.Id, new RequestInput(new WorkerCaller(Team, a.Id, a.Instance),
                InputRequestKind.Question, "a?")));
        var b = await SeedWorkingAsync(db, "b");
        Assert.IsType<StoreResult.Applied>(await b.Store.ApplyAsync(
            b.Id, new RequestInput(new WorkerCaller(Team, b.Id, b.Instance),
                InputRequestKind.Question, "b?")));

        var filtered = (await NewStore(db).GetLeadInboxAsync(Team, a.Id.Value)).Items;
        Assert.True(HaikuSlug.IsWellFormed(Assert.Single(filtered).SessionId));
        Assert.Equal(2, (await NewStore(db).GetLeadInboxAsync(Team)).Items.Count);
    }

    [SkippableFact]
    public async Task Another_teams_sessions_do_not_appear()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var other = TeamId.New();
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(other), other, "secret", "default"));
        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(Machine(), instance));
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            created.Session.Id,
            new RequestInput(new WorkerCaller(other, created.Session.Id, instance),
                InputRequestKind.Question, "leak?")));

        Assert.Empty((await NewStore(db).GetLeadInboxAsync(Team)).Items);
        Assert.Single((await NewStore(db).GetLeadInboxAsync(other)).Items);
    }

    private async Task<(SessionId Id, WorkerInstanceId Instance, SessionStore Store)> SeedWorkingAsync(
        LandbridgeDbContext db, string description = "work")
    {
        var store = NewStore(db);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(Lead, Team, description, "default"));
        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(Machine(), instance));
        return (created.Session.Id, instance, store);
    }
}
