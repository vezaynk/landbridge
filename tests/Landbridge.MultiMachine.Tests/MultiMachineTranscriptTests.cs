using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// The §12 transcript crown, no fakes on any surface that matters: a real <c>landbridged</c>
/// supervisor spawns a real process whose stdout is really captured to disk, and the real
/// plane-side read path pulls it back range by range over the runner-channel seam. Capture,
/// storage, the terminal gate, the pull protocol, and the relay are all the production code.
///
/// <para>The assertion is deliberately uncomfortable: a credential-shaped string the worker
/// printed comes back <b>intact</b>. That is the documented behavior — Landbridge does not redact
/// transcripts (§13, §16 open question 8) — and pinning it here means a future change that
/// starts filtering, or one that widens who may read, has to come through this test.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MultiMachineTranscriptTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>Credential-shaped and unmistakable, so "it came back verbatim" cannot pass by
    /// accident. Not a real token — the shape is what matters (<c>lbr_w_</c>, §5).</summary>
    private const string PlantedSecret = "lbr_w_deadbeefcafe1234567890abcdef";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task A_completed_tasks_captured_transcript_is_read_back_verbatim_from_its_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        await using var rig = new FleetRig(pg);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        // A real worker on machine A prints the planted secret to stdout, which landbridged's
        // capture tees to <state>/transcripts/<task>/0001.ndjson, then reports.
        var task = await rig.CreateSessionAsync($"echo:{PlantedSecret}", ct);
        await rig.DispatchToAsync("A", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(async () => await rig.StateAsync(task, ct) == SessionState.Verifying, Bound),
            "the echo worker never reached verifying. " + await rig.DiagnoseAsync(task, ct));
        Assert.Equal("A", rig.MachineRanOn(task));

        var relay = rig.PlaneServices.GetRequiredService<TranscriptRelayService>();

        // While it is still verifying, the gate refuses: report_result does not revoke the
        // reporting instance's token, so an in-flight transcript can still carry a live
        // credential (§12/§13).
        var tooEarly = await relay.ListAsync(task, "A", ct);
        Assert.Equal(
            TranscriptUnavailable.NotTerminal,
            Assert.IsType<TranscriptResult.Unavailable>(tooEarly).Reason);

        // The Lead accepts → completed → readable.
        await rig.AcceptAsync(task, ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(async () => await rig.StateAsync(task, ct) == SessionState.Completed, Bound),
            "the task never reached completed. " + await rig.DiagnoseAsync(task, ct));

        // The plane finds the machine from the dispatch record, not from live tracking — the
        // task has exited and been untracked by now, which is exactly the case the
        // worker-instance machine column exists for (§12).
        await using var db = pg.NewContext();
        var queries = new DashboardQueries(db, rig.PlaneServices.GetRequiredService<RunnerConnectionRegistry>());
        var locations = await queries.GetTranscriptLocationsAsync(task.Value, ct);
        var location = Assert.Single(locations);
        Assert.Equal("A", location.Machine);
        Assert.True(location.Connected);

        var inventory = Assert.IsType<TranscriptResult.Inventory>(await relay.ListAsync(task, "A", ct));
        var instance = Assert.Single(inventory.Instances);
        Assert.Equal(1, instance.Ordinal);
        Assert.True(instance.StdoutBytes > 0, "the worker's stdout should have been captured");

        // Drain it the way the dashboard does: follow the cursor, one small range at a time,
        // so reassembly across many ranges is what is actually proven.
        var text = await DrainAsync(relay, task, "A", instance.Ordinal, maxBytes: 8, ct);

        Assert.Contains(PlantedSecret, text);

        // Machine B ran nothing for this task and says so — the transcript is not fleet-wide
        // state, it is one machine's disk.
        var wrongMachine = await relay.ListAsync(task, "B", ct);
        Assert.Equal(
            TranscriptUnavailable.MachineRefused,
            Assert.IsType<TranscriptResult.Unavailable>(wrongMachine).Reason);
    }

    private static async Task<string> DrainAsync(
        TranscriptRelayService relay, SessionId task, string machine, int ordinal, int maxBytes,
        CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        var offset = 0L;
        for (var guard = 0; guard < 10_000; guard++)
        {
            var result = await relay.ReadAsync(
                task, machine, ordinal, Landbridge.Contracts.TranscriptStreams.Stdout, offset, maxBytes, ct);
            var range = Assert.IsType<TranscriptResult.Range>(result);
            text.Append(range.Text);
            if (range.Eof)
                return text.ToString();
            Assert.True(range.NextOffset > offset, "the cursor must advance while bytes remain");
            offset = range.NextOffset;
        }
        Assert.Fail("drain did not terminate");
        return "";
    }
}
