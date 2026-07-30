using Docket.Core;

namespace Docket.Contracts.Tests;

/// <summary>
/// The frozen contract's wire boundary, spec §10: both sides encode and decode
/// through <see cref="RunnerWire"/>, and a runner rejects anything outside the
/// vocabulary. Every command, event, and the heartbeat round-trips; an unknown
/// <c>type</c> decodes to <c>null</c> rather than being guessed at.
/// </summary>
public class RunnerWireTests
{
    // ── Commands ────────────────────────────────────────────────────────────

    [Fact]
    public void Dispatch_command_round_trips_with_all_fields()
    {
        var original = new DispatchCommand(
            TaskId.New(),
            "restricted",
            WorkerToken: "dkt_w_abc",
            McpConfigJson: """{"mcpServers":{}}""",
            BudgetUsd: 12.50m,
            SpawnSubstitutions: new Dictionary<string, string> { ["seed"] = "42", ["mode"] = "headless" });

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        // Field-by-field: record value-equality compares the Dictionary member
        // by reference, so the collection is asserted on its contents.
        Assert.Equal(original.Task, decoded.Task);
        Assert.Equal(original.Profile, decoded.Profile);
        Assert.Equal(original.WorkerToken, decoded.WorkerToken);
        Assert.Equal(original.McpConfigJson, decoded.McpConfigJson);
        Assert.Equal(original.BudgetUsd, decoded.BudgetUsd);
        Assert.Equal(original.SpawnSubstitutions, decoded.SpawnSubstitutions);
    }

    [Fact]
    public void Dispatch_command_round_trips_with_only_required_fields()
    {
        var original = new DispatchCommand(TaskId.New(), "default");

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal("", decoded.WorkerToken);
        Assert.Null(decoded.McpConfigJson);
        Assert.Null(decoded.BudgetUsd);
        Assert.Null(decoded.SpawnSubstitutions);
        Assert.Null(decoded.ResumeSessionRef);
    }

    [Fact]
    public void Dispatch_command_round_trips_with_a_resume_session_ref()
    {
        // §11 resume: the opaque session ref rides the dispatch envelope so the
        // runner can continue a parked transcript.
        var original = new DispatchCommand(
            TaskId.New(), "default", WorkerToken: "dkt_w_abc", ResumeSessionRef: "sess-a4bbb0fd");

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal("sess-a4bbb0fd", decoded.ResumeSessionRef);
    }

    [Fact]
    public void Dispatch_envelope_without_a_resume_session_ref_decodes_back_compatibly()
    {
        // A dispatch envelope from a sender that predates the resume field (or a
        // first, never-parked dispatch) carries none; it must decode to a null
        // ResumeSessionRef, never crash (§11 — the addition is wire-compatible,
        // exactly like the OpenForward relay fields).
        var task = TaskId.New();
        var legacy = $$"""
            { "type": "dispatch", "task": { "value": "{{task.Value}}" }, "profile": "default" }
            """;

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(legacy));

        Assert.Equal(task, decoded.Task);
        Assert.Equal("default", decoded.Profile);
        Assert.Null(decoded.ResumeSessionRef);
    }

    [Fact]
    public void Stop_command_round_trips_including_ttl_and_disposition()
    {
        var original = new StopCommand(TaskId.New(), TimeSpan.FromSeconds(30), StopDisposition.PreserveAndPark, "lead asked");

        var decoded = Assert.IsType<StopCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal(TimeSpan.FromSeconds(30), decoded.Ttl);
        Assert.Equal(StopDisposition.PreserveAndPark, decoded.Disposition);
    }

    [Fact]
    public void Kill_command_round_trips()
    {
        var original = new KillCommand(TaskId.New());

        var decoded = Assert.IsType<KillCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Open_forward_command_round_trips_with_only_the_frozen_required_fields()
    {
        var original = new OpenForwardCommand(TaskId.New(), "fwd-1", "postgres");

        var decoded = Assert.IsType<OpenForwardCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        // The increment-3 additions default to empty/0 when the sender set none.
        Assert.Equal("", decoded.Role);
        Assert.Equal("", decoded.Grant);
        Assert.Equal("", decoded.RelayUrl);
        Assert.Equal(0, decoded.Port);
    }

    [Fact]
    public void Open_forward_command_round_trips_with_the_added_data_plane_fields()
    {
        var original = new OpenForwardCommand(
            TaskId.New(), "fwd-1", "postgres",
            Role: "producer", Grant: "dkt_g_abc", RelayUrl: "http://127.0.0.1:5100", Port: 5432);

        var decoded = Assert.IsType<OpenForwardCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal("producer", decoded.Role);
        Assert.Equal("dkt_g_abc", decoded.Grant);
        Assert.Equal("http://127.0.0.1:5100", decoded.RelayUrl);
        Assert.Equal(5432, decoded.Port);
    }

    [Fact]
    public void Open_forward_envelope_without_the_new_fields_decodes_back_compatibly()
    {
        // An envelope from a pre-increment-3 sender carries only the frozen
        // fields; the added ones must decode to empty Role / 0 Port, never crash
        // (§10, §8.3 — additions are wire-compatible).
        var task = TaskId.New();
        var legacy = $$"""
            { "type": "open-forward", "task": { "value": "{{task.Value}}" },
              "forward_id": "fwd-legacy", "service_name": "postgres" }
            """;

        var decoded = Assert.IsType<OpenForwardCommand>(RunnerWire.DecodeCommand(legacy));

        Assert.Equal(task, decoded.Task);
        Assert.Equal("fwd-legacy", decoded.ForwardId);
        Assert.Equal("postgres", decoded.ServiceName);
        Assert.True(string.IsNullOrEmpty(decoded.Role));
        Assert.Equal(0, decoded.Port);
    }

    // ── Traceparent: opaque transport metadata on the envelope (§1 tracing) ───

    [Fact]
    public void Traceparent_round_trips_on_a_dispatch_envelope()
    {
        const string traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var original = new DispatchCommand(TaskId.New(), "default", WorkerToken: "dkt_w_abc");

        var encoded = RunnerWire.EncodeCommand(original, traceparent);
        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(encoded, out var decodedTp));

        Assert.Equal(traceparent, decodedTp);
        // The domain record is untouched by the envelope's transport metadata.
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Command_encoded_without_a_traceparent_decodes_to_null()
    {
        var original = new DispatchCommand(TaskId.New(), "default");

        // Default (no traceparent) encode is byte-for-byte the old envelope.
        var withoutTp = RunnerWire.EncodeCommand(original);
        Assert.DoesNotContain("traceparent", withoutTp);

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(withoutTp, out var decodedTp));
        Assert.Null(decodedTp);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Traceparent_does_not_change_the_decoded_domain_record()
    {
        const string traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var original = new DispatchCommand(
            TaskId.New(), "restricted", WorkerToken: "dkt_w_x", BudgetUsd: 5m);

        var withTp = Assert.IsType<DispatchCommand>(
            RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original, traceparent)));
        var withoutTp = Assert.IsType<DispatchCommand>(
            RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        // Same record whether or not the envelope carried a traceparent.
        Assert.Equal(withoutTp, withTp);
        Assert.Equal(original, withTp);
    }

    [Fact]
    public void The_single_arg_decode_still_works_alongside_the_traceparent_overload()
    {
        var original = new KillCommand(TaskId.New());
        var encoded = RunnerWire.EncodeCommand(
            original, "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        // The convenience overload discards the traceparent but still decodes.
        var decoded = Assert.IsType<KillCommand>(RunnerWire.DecodeCommand(encoded));
        Assert.Equal(original, decoded);
    }

    // ── Events ──────────────────────────────────────────────────────────────

    [Fact]
    public void Started_event_round_trips()
    {
        var original = new StartedEvent(TaskId.New(), DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<StartedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Session_started_event_round_trips_including_the_session_ref()
    {
        var original = new SessionStartedEvent(TaskId.New(), "sess-a4bbb0fd", DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<SessionStartedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
        Assert.Equal("sess-a4bbb0fd", decoded.SessionRef);
    }

    [Fact]
    public void Alive_event_round_trips()
    {
        var original = new AliveEvent(TaskId.New(), DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<AliveEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Tool_call_event_round_trips()
    {
        var original = new ToolCallEvent(TaskId.New(), "Bash", DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<ToolCallEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Subagent_spawned_event_round_trips_including_nulls()
    {
        var original = new SubagentSpawnedEvent(TaskId.New(), "agent-2", null, DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<SubagentSpawnedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
        Assert.Null(decoded.ParentAgentId);
    }

    [Fact]
    public void Exited_event_round_trips()
    {
        var original = new ExitedEvent(TaskId.New(), 137, DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<ExitedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Auth_failed_event_round_trips()
    {
        var original = new AuthFailedEvent(TaskId.New(), "clone", "git@host:repo", "403", "repo:read");
        var decoded = Assert.IsType<AuthFailedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Forward_opened_event_round_trips_including_the_bound_port()
    {
        var original = new ForwardOpenedEvent(TaskId.New(), "fwd-8", 54321);
        var decoded = Assert.IsType<ForwardOpenedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
        Assert.Equal(54321, decoded.Port);
    }

    [Fact]
    public void Forward_closed_event_round_trips()
    {
        var original = new ForwardClosedEvent(TaskId.New(), "fwd-9");
        var decoded = Assert.IsType<ForwardClosedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Rebooted_event_round_trips_as_the_machine_scoped_message()
    {
        var original = new RebootedEvent("machine-7", DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<RebootedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    [Fact]
    public void Heartbeat_round_trips_including_load_and_profiles()
    {
        var original = new MachineHeartbeat(
            "machine-1",
            Ready: true,
            UnderBackPressure: false,
            new SystemLoad(0.1, 0.2, 0.3),
            RunningTasks: 4,
            Profiles: ["default", "restricted"],
            At: DateTimeOffset.UtcNow);

        var decoded = RunnerWire.DecodeHeartbeat(RunnerWire.EncodeHeartbeat(original));

        Assert.NotNull(decoded);
        Assert.Equal("machine-1", decoded!.MachineId);
        Assert.True(decoded.Ready);
        Assert.Equal(new SystemLoad(0.1, 0.2, 0.3), decoded.Load);
        Assert.Equal(4, decoded.RunningTasks);
        Assert.Equal(new[] { "default", "restricted" }, decoded.Profiles);
        Assert.Equal(original.At, decoded.At);
    }

    // ── Rejection: anything outside the vocabulary (§10) ──────────────────────

    [Fact]
    public void Decode_command_rejects_unknown_type_and_malformed_json()
    {
        Assert.Null(RunnerWire.DecodeCommand("""{ "type": "frobnicate", "task": { "value": "x" } }"""));
        Assert.Null(RunnerWire.DecodeCommand("""{ "task": { "value": "x" } }"""));
        Assert.Null(RunnerWire.DecodeCommand("not json at all"));
        // An event on the command channel is not a command.
        Assert.Null(RunnerWire.DecodeCommand(RunnerWire.EncodeEvent(new StartedEvent(TaskId.New(), DateTimeOffset.UtcNow))));
    }

    [Fact]
    public void Decode_event_rejects_unknown_type_and_commands()
    {
        Assert.Null(RunnerWire.DecodeEvent("""{ "type": "frobnicate" }"""));
        Assert.Null(RunnerWire.DecodeEvent("not json at all"));
        // A command on the event channel is not an event.
        Assert.Null(RunnerWire.DecodeEvent(RunnerWire.EncodeCommand(new KillCommand(TaskId.New()))));
    }

    [Fact]
    public void Decode_heartbeat_rejects_non_heartbeat_envelopes()
    {
        Assert.Null(RunnerWire.DecodeHeartbeat(RunnerWire.EncodeEvent(new StartedEvent(TaskId.New(), DateTimeOffset.UtcNow))));
        Assert.Null(RunnerWire.DecodeHeartbeat("""{ "type": "kill", "task": { "value": "x" } }"""));
        Assert.Null(RunnerWire.DecodeHeartbeat("not json at all"));
    }

    [Fact]
    public void Vocabulary_sets_are_the_closed_frozen_lists()
    {
        Assert.Equal(
            new HashSet<string> { "dispatch", "stop", "kill", "open-forward" },
            new HashSet<string>(RunnerWire.Commands));
        Assert.Equal(
            new HashSet<string> { "started", "session-started", "alive", "tool-call", "subagent-spawned", "exited", "auth-failed", "forward-opened", "forward-closed", "rebooted" },
            new HashSet<string>(RunnerWire.Events));
    }
}
