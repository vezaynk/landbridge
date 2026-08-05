using System.Text.Json;
using Docket.Contracts;
using Docket.Core;
using Docket.ControlPlane;
using Docket.ControlPlane.Tests;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The four §10 process tools over a <b>real MCP connection</b> — `start_process`,
/// `write_process`, `list_processes`, `stop_process` — driven with a real worker credential
/// against a real plane, with a stub machine standing in for docketd's side of the
/// request/reply.
///
/// <para>The centrepiece is
/// <see cref="A_process_survives_its_declaring_task_and_a_later_task_stops_it"/>, which is the
/// user's ruling made executable: a process outlives the task that started it, and a
/// <em>different</em> task — the Lead's cleanup continuation — is what stops it. Everything
/// else here is a gate or a shape; that one is the design.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProcessToolsEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A stub machine that answers the process commands the way a real docketd would, and
    /// remembers what it was told. Keyed by name, so it can model the one fact the tools depend
    /// on: a process keeps existing after the task that started it is gone.
    /// </summary>
    private sealed class StubMachine(RunnerEventSink sink)
    {
        private readonly Dictionary<string, int?> _live = new(StringComparer.Ordinal);

        public List<string> Written { get; } = [];

        public IReadOnlyDictionary<string, int?> Live => _live;

        public Func<RunnerCommand, CancellationToken, Task> Send => async (command, ct) =>
        {
            switch (command)
            {
                case StartProcessCommand s:
                    if (_live.ContainsKey(s.Name))
                    {
                        await sink.HandleAsync(new ProcessStartedEvent(
                            s.Task, s.RequestId, s.Name, false, ProcessRefusals.NameTaken), ct);
                        return;
                    }
                    _live[s.Name] = null;
                    await sink.HandleAsync(new ProcessStartedEvent(
                        s.Task, s.RequestId, s.Name, true,
                        LogPath: $"/state/processes/{s.Name}"), ct);
                    return;

                case WriteProcessCommand w:
                    if (!_live.ContainsKey(w.Name))
                    {
                        await sink.HandleAsync(new ProcessWrittenEvent(
                            w.Task, w.RequestId, w.Name, false, 0, ProcessRefusals.NoSuchProcess), ct);
                        return;
                    }
                    Written.Add(w.AppendNewline ? w.Data + "\n" : w.Data);
                    await sink.HandleAsync(new ProcessWrittenEvent(
                        w.Task, w.RequestId, w.Name, true, Written[^1].Length), ct);
                    return;

                case StopProcessCommand t:
                    if (!_live.Remove(t.Name))
                    {
                        await sink.HandleAsync(new ProcessStoppedEvent(
                            t.Task, t.RequestId, t.Name, false, null, ProcessRefusals.NoSuchProcess), ct);
                        return;
                    }
                    await sink.HandleAsync(new ProcessStoppedEvent(
                        t.Task, t.RequestId, t.Name, true, ExitCode: 0), ct);
                    return;
            }
        };
    }

    [SkippableFact]
    public async Task A_worker_starts_a_process_and_is_told_the_log_path_and_what_to_do_next()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var worker = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var machine = new StubMachine(plane.Services.GetRequiredService<RunnerEventSink>());
        registry.Register("m1", new HashSet<string> { "default" }, machine.Send);
        registry.TrackDispatch("m1", worker.Task);

        await using var client = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), worker.Token, ct);

        var payload = await CallAsync(client, "start_process", new Dictionary<string, object?>
        {
            ["name"] = "web-dev",
            ["spawn"] = new[] { "/bin/npm", "run", "dev" },
        }, ct);

        Assert.True(payload.GetProperty("started").GetBoolean());
        Assert.Contains("web-dev", payload.GetProperty("logPath").GetString()!, StringComparison.Ordinal);
        // Ports are out of scope: the reply must carry no port at all, so nobody reads one into it.
        Assert.False(payload.TryGetProperty("port", out _));
        // Guidance: Docket tracks no port, registering is separate, and nothing stops it for you.
        var next = payload.GetProperty("nextStep").GetString()!;
        Assert.Contains("does not track this process's port", next, StringComparison.Ordinal);
        Assert.Contains("register_service", next, StringComparison.Ordinal);
        Assert.Contains("stop_process", next, StringComparison.Ordinal);

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Starting_with_closed_stdin_is_reflected_in_the_guidance()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var worker = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var machine = new StubMachine(plane.Services.GetRequiredService<RunnerEventSink>());
        registry.Register("m1", new HashSet<string> { "default" }, machine.Send);
        registry.TrackDispatch("m1", worker.Task);

        await using var client = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), worker.Token, ct);

        var payload = await CallAsync(client, "start_process", new Dictionary<string, object?>
        {
            ["name"] = "batch",
            ["spawn"] = new[] { "/bin/batch" },
            ["openStdin"] = false,
        }, ct);

        Assert.True(payload.GetProperty("started").GetBoolean());
        // An agent that closed stdin must be told what it gave up, at the moment it gave it up —
        // not left to discover it when write_process refuses later.
        var next = payload.GetProperty("nextStep").GetString()!;
        Assert.Contains("stdin closed", next, StringComparison.Ordinal);
        Assert.Contains("hard stop", next, StringComparison.Ordinal);

        await plane.StopAsync(ct);
    }

    /// <summary>
    /// <b>The ruling, executable.</b> A process outlives the task that started it, and a
    /// different task — standing in for the Lead's cleanup continuation — is what stops it. If
    /// lifetime were task-scoped, the second half of this test could not exist.
    /// </summary>
    [SkippableFact]
    public async Task A_process_survives_its_declaring_task_and_a_later_task_stops_it()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var starter = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);
        var cleaner = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var machine = new StubMachine(plane.Services.GetRequiredService<RunnerEventSink>());
        registry.Register("m1", new HashSet<string> { "default" }, machine.Send);
        registry.TrackDispatch("m1", starter.Task);
        registry.TrackDispatch("m1", cleaner.Task);

        // 1. The first task starts a long-running process.
        await using (var first = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), starter.Token, ct))
        {
            var started = await CallAsync(first, "start_process", new Dictionary<string, object?>
            {
                ["name"] = "long-build",
                ["spawn"] = new[] { "/bin/make", "all" },
            }, ct);
            Assert.True(started.GetProperty("started").GetBoolean());
        }

        // 2. Its task is gone from the plane's point of view — untracked, as a terminal task is.
        //    Nothing about that touches the process.
        registry.Untrack(starter.Task);
        Assert.True(machine.Live.ContainsKey("long-build"),
            "the process must survive its declaring task — that is the whole point of the feature");

        // list_processes reads what the machine last REPORTED, so it is heartbeat-fresh rather
        // than instant — the plane never holds process state of its own. Beat once, as a real
        // machine does every 15s.
        registry.ApplyHeartbeat("m1", new MachineHeartbeat(
            "m1", Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
            RunningTasks: 1, ["default"], DateTimeOffset.UtcNow,
            Processes: [new ProcessStatus("long-build", ServiceState.Running, starter.Task.Value)]));

        // 3. The cleanup task — a DIFFERENT task id, as a Lead's continuation carries — can see
        //    it and stop it. This is the half that task-scoped lifetime would have made impossible.
        await using (var second = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), cleaner.Token, ct))
        {
            // Discovery is the load-bearing half of the cleanup story: a continuation that has
            // lost its notes cannot be handed the name, so it must find the survivor itself.
            // The name below is taken FROM the list result, never from this test's knowledge —
            // if list_processes were a stub, the stop could not be addressed at all.
            var listed = await CallAsync(second, "list_processes", new Dictionary<string, object?>(), ct);
            var survivor = Assert.Single(
                listed.EnumerateArray().ToList(), e => e.GetProperty("kind").GetString() == "process");
            var discoveredName = survivor.GetProperty("name").GetString()!;
            Assert.Equal("long-build", discoveredName); // sanity: it found the right one
            Assert.Equal("running", survivor.GetProperty("state").GetString());
            // A process carries no port — absent or null, never a number. (The serializer omits
            // nulls, so the property may not be there at all; asserting the absence directly is
            // the honest check.)
            Assert.True(!survivor.TryGetProperty("port", out var portEl)
                        || portEl.ValueKind == JsonValueKind.Null);
            Assert.True(survivor.GetProperty("stdinOpen").GetBoolean());

            var stopped = await CallAsync(second, "stop_process", new Dictionary<string, object?>
            {
                ["name"] = discoveredName,
            }, ct);
            Assert.True(stopped.GetProperty("ok").GetBoolean());
            Assert.Equal(0, stopped.GetProperty("value").GetInt32()); // the exit, recorded
        }

        Assert.False(machine.Live.ContainsKey("long-build"));

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_write_reaches_the_process_and_an_unknown_name_is_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var worker = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var machine = new StubMachine(plane.Services.GetRequiredService<RunnerEventSink>());
        registry.Register("m1", new HashSet<string> { "default" }, machine.Send);
        registry.TrackDispatch("m1", worker.Task);

        await using var client = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), worker.Token, ct);

        await CallAsync(client, "start_process", new Dictionary<string, object?>
        {
            ["name"] = "repl",
            ["spawn"] = new[] { "/bin/python" },
        }, ct);

        var written = await CallAsync(client, "write_process", new Dictionary<string, object?>
        {
            ["name"] = "repl",
            ["data"] = "2 + 2",
        }, ct);
        Assert.True(written.GetProperty("ok").GetBoolean());
        // Newline appended by default: line-oriented is the overwhelming case.
        Assert.Equal("2 + 2\n", Assert.Single(machine.Written));

        var refused = await CallAsync(client, "write_process", new Dictionary<string, object?>
        {
            ["name"] = "ghost",
            ["data"] = "hello",
        }, ct);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal(ProcessRefusals.NoSuchProcess, refused.GetProperty("refusal").GetString());

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_task_no_machine_holds_is_told_so_rather_than_hanging()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var workerToken = await RelayGrantTestKit.SeedWorkingWorkerTokenAsync(pg, team, ct);
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        // No registered machine and no tracked dispatch: a process lives on a machine, so a task
        // that is nowhere has nowhere to start one. It must say that, not wait for a timeout.
        await using var client = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), workerToken, ct);

        var payload = await CallAsync(client, "start_process", new Dictionary<string, object?>
        {
            ["name"] = "orphan",
            ["spawn"] = new[] { "/bin/true" },
        }, ct);

        Assert.False(payload.GetProperty("started").GetBoolean());
        Assert.Contains("not currently held by any machine",
            payload.GetProperty("refusal").GetString()!, StringComparison.Ordinal);

        // And the list read is empty rather than an error.
        var listed = await CallAsync(client, "list_processes", new Dictionary<string, object?>(), ct);
        Assert.Empty(listed.EnumerateArray());

        await plane.StopAsync(ct);
    }

    private static async Task<JsonElement> CallAsync(
        McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent is { } structured
            ? structured.GetRawText()
            : string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
