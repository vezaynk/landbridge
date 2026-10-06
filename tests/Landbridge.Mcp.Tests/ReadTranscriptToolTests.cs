using System.Text;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// <c>read_transcript</c> on the owning Lead: one bounded range of a session that is still
/// running, refused for a session on another Team, and a tail when the offset is omitted.
/// The fake machine answers through the real sink, the same path Core uses.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReadTranscriptToolTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly Guid Machine = TestMachineIds.For("transcript-tool");
    private const string Secret = "lbr_w_deadbeefcafe1234";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task The_owning_lead_reads_a_live_transcript_and_another_team_cannot()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var team = TeamId.New();
        var otherTeam = TeamId.New();
        var lead = await LeadFactory.SeedAsync(pg, team, TimeProvider.System);
        var other = await LeadFactory.SeedAsync(pg, otherTeam, TimeProvider.System);
        var (session, content) = await SeedWorkingAsync(team);
        var rig = OpenRig(content);
        // HttpContextAccessor stores the request in an async-local shared by every instance.
        // One accessor, and the principal is stamped immediately before each call.
        var http = new HttpContextAccessor();
        var tools = ToolsFor(http, rig);

        SignIn(http, lead);
        var read = await tools.ReadTranscript(
            session.Value.ToString("D"), team.Value.ToString("D"), CancellationToken.None);

        Assert.Contains(Secret, read.Text);
        Assert.True(read.Running);
        Assert.True(read.Eof);
        Assert.Equal(1, read.Ordinal);
        Assert.Equal(TranscriptStreams.Stdout, read.Stream);

        var before = rig.Sent.Count;
        SignIn(http, other);
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.ReadTranscript(
            session.Value.ToString("D"), otherTeam.Value.ToString("D"), CancellationToken.None));
        Assert.Contains("not a session on this team", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, rig.Sent.Count);

        SignIn(http, lead);
        var inventory = await tools.ReadTranscript(
            session.Value.ToString("D"), team.Value.ToString("D"), CancellationToken.None, ordinal: 0);
        Assert.Equal("", inventory.Text);
        var instance = Assert.Single(inventory.Instances!);
        Assert.Equal(1, instance.Ordinal);
        Assert.True(instance.StdoutBytes > 0);

        var tail = await tools.ReadTranscript(
            session.Value.ToString("D"), team.Value.ToString("D"), CancellationToken.None, maxBytes: 8);
        Assert.Equal("aaaaTAIL", tail.Text);
        Assert.True(tail.Offset > 0);
        Assert.DoesNotContain(Secret, tail.Text, StringComparison.Ordinal);
    }

    private async Task<(SessionId Id, string Content)> SeedWorkingAsync(TeamId team)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "criteria", Profile: "default"));
        await store.DispatchNextAsync(
            new MachineSnapshot(Machine, Ready: true, UnderBackPressure: false, new HashSet<string> { "default" }),
            WorkerInstanceId.New());
        return (created.Session.Id, Secret + new string('a', 100) + "TAIL");
    }

    private sealed record Rig(TranscriptRelayService Relay, RunnerConnectionRegistry Registry, List<ReadTranscriptCommand> Sent);

    private Rig OpenRig(string content)
    {
        var clock = TimeProvider.System;
        var registry = new RunnerConnectionRegistry(clock);
        var waiters = new TranscriptWaiters();
        var scopes = ScopeFactory(clock);
        var sink = new RunnerEventSink(
            scopes, registry, new ForwardWaiters(), waiters,
            new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        var relay = new TranscriptRelayService(
            scopes, registry, waiters, NullLogger<TranscriptRelayService>.Instance, clock);
        var sent = new List<ReadTranscriptCommand>();
        var bytes = Encoding.UTF8.GetBytes(content);
        registry.Register(Machine, new HashSet<string> { "default" }, async (command, ct) =>
        {
            if (command is not ReadTranscriptCommand read)
                return;
            sent.Add(read);
            TranscriptChunkEvent reply;
            if (read.Ordinal == 0)
            {
                reply = new TranscriptChunkEvent(
                    read.Session, read.RequestId, Eof: true,
                    Instances: [new TranscriptInstance(1, bytes.Length, 0, DateTimeOffset.UtcNow)]);
            }
            else
            {
                var offset = (int)Math.Min(read.Offset, bytes.Length);
                var take = Math.Min(read.MaxBytes, bytes.Length - offset);
                reply = new TranscriptChunkEvent(
                    read.Session, read.RequestId,
                    Text: Encoding.UTF8.GetString(bytes, offset, take),
                    NextOffset: offset + take,
                    Eof: offset + take >= bytes.Length);
            }
            await sink.HandleAsync(reply, ct);
        });
        return new Rig(relay, registry, sent);
    }

    private static void SignIn(IHttpContextAccessor http, Principal principal) =>
        http.HttpContext = new DefaultHttpContext { User = LandbridgeClaims.ToClaimsPrincipal(principal) };

    private LeadTools ToolsFor(IHttpContextAccessor http, Rig rig) =>
        RelayGrantTestKit.LeadToolsFor(
            pg.NewContext(), TimeProvider.System, rig.Registry, http, transcripts: rig.Relay);

    private IServiceScopeFactory ScopeFactory(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddLandbridgeStore();
        services.AddScoped<TokenService>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
