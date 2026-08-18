using Landbridge.Core;

namespace Landbridge.Contracts.Tests;

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
            SessionId.New(),
            "restricted",
            WorkerToken: "lbr_w_abc",
            McpConfigJson: """{"mcpServers":{}}""",
            SpawnSubstitutions: new Dictionary<string, string> { ["seed"] = "42", ["mode"] = "headless" });

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        // Field-by-field: record value-equality compares the Dictionary member
        // by reference, so the collection is asserted on its contents.
        Assert.Equal(original.Session, decoded.Session);
        Assert.Equal(original.Profile, decoded.Profile);
        Assert.Equal(original.WorkerToken, decoded.WorkerToken);
        Assert.Equal(original.McpConfigJson, decoded.McpConfigJson);
        Assert.Equal(original.SpawnSubstitutions, decoded.SpawnSubstitutions);
    }

    [Fact]
    public void Dispatch_command_round_trips_with_only_required_fields()
    {
        var original = new DispatchCommand(SessionId.New(), "default");

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal("", decoded.WorkerToken);
        Assert.Null(decoded.McpConfigJson);
        Assert.Null(decoded.SpawnSubstitutions);
        Assert.Null(decoded.ResumeSessionRef);
    }

    [Fact]
    public void Dispatch_command_round_trips_with_a_resume_session_ref()
    {
        // §11 resume: the opaque session ref rides the dispatch envelope so the
        // runner can continue a parked transcript.
        var original = new DispatchCommand(
            SessionId.New(), "default", WorkerToken: "lbr_w_abc", ResumeSessionRef: "sess-a4bbb0fd");

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
        var task = SessionId.New();
        var legacy = $$"""
            { "type": "dispatch", "session": { "value": "{{task.Value}}" }, "profile": "default" }
            """;

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(legacy));

        Assert.Equal(task, decoded.Session);
        Assert.Equal("default", decoded.Profile);
        Assert.Null(decoded.ResumeSessionRef);
    }

    [Fact]
    public void Dispatch_command_round_trips_with_a_work_dir_task()
    {
        // §7/§11: a continuation runs in its predecessor's work dir, so the dispatch names
        // that task (a task, never a path — work_root is machine-local runner config).
        var from = SessionId.New();
        var original = new DispatchCommand(
            SessionId.New(), "default", WorkerToken: "lbr_w_abc",
            ResumeSessionRef: "sess-a4bbb0fd", WorkDirSession: from);

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal(from, decoded.WorkDirSession);
    }

    [Fact]
    public void Dispatch_envelope_without_a_work_dir_task_decodes_back_compatibly()
    {
        // A sender that predates the field — and every ordinary task, which needs none —
        // carries no work_dir_task; it must decode to null, never crash.
        var task = SessionId.New();
        var legacy = $$"""
            { "type": "dispatch", "session": { "value": "{{task.Value}}" }, "profile": "default",
              "resume_session_ref": "sess-1" }
            """;

        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(legacy));

        Assert.Equal(task, decoded.Session);
        Assert.Equal("sess-1", decoded.ResumeSessionRef);
        Assert.Null(decoded.WorkDirSession);
    }

    [Fact]
    public void Stop_command_round_trips_including_ttl_and_disposition()
    {
        var original = new StopCommand(SessionId.New(), TimeSpan.FromSeconds(30), StopDisposition.PreserveAndPark, "lead asked");

        var decoded = Assert.IsType<StopCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal(TimeSpan.FromSeconds(30), decoded.Ttl);
        Assert.Equal(StopDisposition.PreserveAndPark, decoded.Disposition);
    }

    [Fact]
    public void Kill_command_round_trips()
    {
        var original = new KillCommand(SessionId.New());

        var decoded = Assert.IsType<KillCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Open_forward_command_round_trips_with_only_the_frozen_required_fields()
    {
        var original = new OpenForwardCommand(SessionId.New(), "fwd-1", "postgres");

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
            SessionId.New(), "fwd-1", "postgres",
            Role: "producer", Grant: "lbr_g_abc", RelayUrl: "http://127.0.0.1:5100", Port: 5432);

        var decoded = Assert.IsType<OpenForwardCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal("producer", decoded.Role);
        Assert.Equal("lbr_g_abc", decoded.Grant);
        Assert.Equal("http://127.0.0.1:5100", decoded.RelayUrl);
        Assert.Equal(5432, decoded.Port);
    }

    [Fact]
    public void Open_forward_envelope_without_the_new_fields_decodes_back_compatibly()
    {
        // An envelope from a pre-increment-3 sender carries only the frozen
        // fields; the added ones must decode to empty Role / 0 Port, never crash
        // (§10, §8.3 — additions are wire-compatible).
        var task = SessionId.New();
        var legacy = $$"""
            { "type": "open-forward", "session": { "value": "{{task.Value}}" },
              "forward_id": "fwd-legacy", "service_name": "postgres" }
            """;

        var decoded = Assert.IsType<OpenForwardCommand>(RunnerWire.DecodeCommand(legacy));

        Assert.Equal(task, decoded.Session);
        Assert.Equal("fwd-legacy", decoded.ForwardId);
        Assert.Equal("postgres", decoded.ServiceName);
        Assert.True(string.IsNullOrEmpty(decoded.Role));
        Assert.Equal(0, decoded.Port);
    }

    [Fact]
    public void Close_forward_command_round_trips()
    {
        var original = new CloseForwardCommand(SessionId.New(), "fwd-1");

        var decoded = Assert.IsType<CloseForwardCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Close_forward_envelope_is_the_documented_snake_case_shape()
    {
        // The envelope a landbridged that predates close-forward will see, spelled out: adding a
        // COMMAND is skew-safe precisely because such a runner rejects the whole envelope at
        // the wire boundary (§10) and goes on serving its splice, which is the pre-fix
        // behaviour rather than a crash. Asserted on the field names because they are what an
        // older/newer peer has to agree about.
        var task = SessionId.New();

        var json = RunnerWire.EncodeCommand(new CloseForwardCommand(task, "fwd-7"));

        Assert.Contains("\"type\":\"close-forward\"", json);
        Assert.Contains("\"forward_id\":\"fwd-7\"", json);
        Assert.Contains(task.Value.ToString(), json);
        // Decoded by hand off the same text, so encode and decode are not each other's alibi.
        var decoded = Assert.IsType<CloseForwardCommand>(RunnerWire.DecodeCommand(
            $$"""{ "type": "close-forward", "session": { "value": "{{task.Value}}" }, "forward_id": "fwd-7" }"""));
        Assert.Equal(task, decoded.Session);
        Assert.Equal("fwd-7", decoded.ForwardId);
    }

    // ── Traceparent: opaque transport metadata on the envelope (§1 tracing) ───

    [Fact]
    public void Traceparent_round_trips_on_a_dispatch_envelope()
    {
        const string traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var original = new DispatchCommand(SessionId.New(), "default", WorkerToken: "lbr_w_abc");

        var encoded = RunnerWire.EncodeCommand(original, traceparent);
        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(encoded, out var decodedTp));

        Assert.Equal(traceparent, decodedTp);
        // The domain record is untouched by the envelope's transport metadata.
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Command_encoded_without_a_traceparent_decodes_to_null()
    {
        var original = new DispatchCommand(SessionId.New(), "default");

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
            SessionId.New(), "restricted", WorkerToken: "lbr_w_x");

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
        var original = new KillCommand(SessionId.New());
        var encoded = RunnerWire.EncodeCommand(
            original, "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        // The convenience overload discards the traceparent but still decodes.
        var decoded = Assert.IsType<KillCommand>(RunnerWire.DecodeCommand(encoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Read_transcript_command_round_trips_with_a_range()
    {
        var original = new ReadTranscriptCommand(
            SessionId.New(), "req-1", Ordinal: 2, Stream: TranscriptStreams.Stderr,
            Offset: 65_536, MaxBytes: 4096);

        var decoded = Assert.IsType<ReadTranscriptCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Read_transcript_command_defaults_to_an_inventory_of_stdout_from_the_start()
    {
        // Ordinal 0 is the inventory request; the rest of the range fields are
        // irrelevant to it and default to the head of stdout (§12).
        var original = new ReadTranscriptCommand(SessionId.New(), "req-1");

        var decoded = Assert.IsType<ReadTranscriptCommand>(RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal(0, decoded.Ordinal);
        Assert.Equal(TranscriptStreams.Stdout, decoded.Stream);
        Assert.Equal(0, decoded.Offset);
        Assert.Equal(TranscriptStreams.DefaultMaxBytes, decoded.MaxBytes);
    }

    // ── Events ──────────────────────────────────────────────────────────────

    [Fact]
    public void Started_event_round_trips()
    {
        var original = new StartedEvent(SessionId.New(), DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<StartedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Session_started_event_round_trips_including_the_session_ref()
    {
        var original = new SessionStartedEvent(SessionId.New(), "sess-a4bbb0fd", DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<SessionStartedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
        Assert.Equal("sess-a4bbb0fd", decoded.SessionRef);
    }

    [Fact]
    public void Alive_event_round_trips()
    {
        var original = new AliveEvent(SessionId.New(), DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<AliveEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Tool_call_event_round_trips()
    {
        var original = new ToolCallEvent(SessionId.New(), "Bash", DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<ToolCallEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Subagent_spawned_event_round_trips_including_nulls()
    {
        var original = new SubagentSpawnedEvent(SessionId.New(), "agent-2", null, DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<SubagentSpawnedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
        Assert.Null(decoded.ParentAgentId);
    }

    [Fact]
    public void Exited_event_round_trips()
    {
        var original = new ExitedEvent(SessionId.New(), 137, DateTimeOffset.UtcNow);
        var decoded = Assert.IsType<ExitedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Auth_failed_event_round_trips()
    {
        var original = new AuthFailedEvent(SessionId.New(), "clone", "git@host:repo", "403", "repo:read");
        var decoded = Assert.IsType<AuthFailedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Forward_opened_event_round_trips_including_the_bound_port()
    {
        var original = new ForwardOpenedEvent(SessionId.New(), "fwd-8", 54321);
        var decoded = Assert.IsType<ForwardOpenedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));
        Assert.Equal(original, decoded);
        Assert.Equal(54321, decoded.Port);
    }

    [Fact]
    public void Forward_closed_event_round_trips()
    {
        var original = new ForwardClosedEvent(SessionId.New(), "fwd-9");
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

    [Fact]
    public void Transcript_chunk_event_round_trips_a_range_of_verbatim_text()
    {
        // Verbatim means exactly that: quotes, backslashes, control characters, and
        // multi-byte characters all survive the JSON envelope unchanged (§12, §13).
        var original = new TranscriptChunkEvent(
            SessionId.New(), "req-1",
            Text: "{\"type\":\"assistant\",\"text\":\"a \\\" quote, a \ttab, an émoji 🛠\"}\n",
            NextOffset: 4096,
            Eof: false);

        var decoded = Assert.IsType<TranscriptChunkEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        Assert.Equal(original, decoded);
        Assert.Null(decoded.Refusal);
        Assert.Null(decoded.Instances);
    }

    [Fact]
    public void Transcript_chunk_event_round_trips_an_inventory()
    {
        var original = new TranscriptChunkEvent(
            SessionId.New(), "req-1",
            Instances:
            [
                new TranscriptInstance(1, 1024, 0, DateTimeOffset.UtcNow),
                new TranscriptInstance(2, 2048, 96, DateTimeOffset.UtcNow),
            ]);

        var decoded = Assert.IsType<TranscriptChunkEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        // Record value-equality compares the list member by reference, so assert contents.
        Assert.Equal(original.RequestId, decoded.RequestId);
        Assert.Equal(original.Instances, decoded.Instances);
        Assert.Equal("", decoded.Text);
    }

    [Fact]
    public void Transcript_chunk_event_round_trips_a_refusal()
    {
        var original = new TranscriptChunkEvent(
            SessionId.New(), "req-1", Refusal: TranscriptRefusals.NoTranscript);

        var decoded = Assert.IsType<TranscriptChunkEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        Assert.Equal(original, decoded);
        Assert.Equal(TranscriptRefusals.NoTranscript, decoded.Refusal);
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
            RunningSessions: 4,
            Profiles: ["default", "restricted"],
            At: DateTimeOffset.UtcNow);

        var decoded = RunnerWire.DecodeHeartbeat(RunnerWire.EncodeHeartbeat(original));

        Assert.NotNull(decoded);
        Assert.Equal("machine-1", decoded!.MachineId);
        Assert.True(decoded.Ready);
        Assert.Equal(new SystemLoad(0.1, 0.2, 0.3), decoded.Load);
        Assert.Equal(4, decoded.RunningSessions);
        Assert.Equal(new[] { "default", "restricted" }, decoded.Profiles);
        Assert.Equal(original.At, decoded.At);
    }

    [Fact]
    public void Heartbeat_carries_the_transcripts_servable_flag_and_defaults_it_false()
    {
        var servable = new MachineHeartbeat(
            "machine-1", Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
            RunningSessions: 0, Profiles: ["default"], At: DateTimeOffset.UtcNow,
            TranscriptsServable: true);

        Assert.True(RunnerWire.DecodeHeartbeat(RunnerWire.EncodeHeartbeat(servable))!.TranscriptsServable);

        // A heartbeat from a runner predating transcript serving omits the property;
        // it must decode to false so the dashboard offers no link (§12).
        var legacy = """
            { "type": "heartbeat", "machine_id": "machine-1", "ready": true,
              "under_back_pressure": false,
              "load": { "cpu_load": 0, "memory_load": 0, "disk_usage": 0 },
              "running_tasks": 0, "profiles": ["default"], "at": "2026-08-02T12:00:00+00:00" }
            """;

        var decoded = RunnerWire.DecodeHeartbeat(legacy);

        Assert.NotNull(decoded);
        Assert.False(decoded!.TranscriptsServable);
    }

    // ── Rejection: anything outside the vocabulary (§10) ──────────────────────

    [Fact]
    public void Usage_reported_event_round_trips_with_every_field()
    {
        var original = new UsageReportedEvent(
            SessionId.New(),
            "claude-sonnet-5[1m]",
            InputTokens: 2,
            OutputTokens: 4,
            CacheReadTokens: 18282,
            CacheWriteTokens: 17178,
            ReasoningOutputTokens: null,
            CostUsd: 0.1086186m,
            At: DateTimeOffset.UtcNow);

        var decoded = Assert.IsType<UsageReportedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        Assert.Equal(original, decoded);
        // The cost must survive as an exact decimal: it is money, and a float round-trip that
        // shifted the last digit would make the reported figure quietly not the harness's.
        Assert.Equal(0.1086186m, decoded.CostUsd);
    }

    [Fact]
    public void Usage_reported_event_round_trips_an_unattributed_tokens_only_report()
    {
        // The Codex shape: real tokens, no model, no cost. Every absence has to survive as an
        // absence — a null cost that decoded as 0 would turn "not measured" into "free".
        var original = new UsageReportedEvent(
            SessionId.New(),
            Model: null,
            InputTokens: 100,
            OutputTokens: 30,
            CacheReadTokens: 900,
            CacheWriteTokens: 50,
            ReasoningOutputTokens: 12,
            CostUsd: null,
            At: DateTimeOffset.UtcNow);

        var decoded = Assert.IsType<UsageReportedEvent>(RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        Assert.Equal(original, decoded);
        Assert.Null(decoded.Model);
        Assert.Null(decoded.CostUsd);
        Assert.Equal(12, decoded.ReasoningOutputTokens);
    }

    [Fact]
    public void An_unnamed_model_rides_the_wire_as_an_absence_not_an_empty_string()
    {
        // Null means "the harness named no model", and it has to survive as null: an empty string
        // would read downstream as a model whose name happens to be blank, and the §12 view would
        // render it as an attribution rather than the honest "not reported".
        var json = RunnerWire.EncodeEvent(new UsageReportedEvent(
            SessionId.New(), Model: null,
            InputTokens: 1, OutputTokens: 1, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningOutputTokens: null, CostUsd: null, At: DateTimeOffset.UtcNow));

        Assert.DoesNotContain("\"model\":\"\"", json);
        var decoded = Assert.IsType<UsageReportedEvent>(RunnerWire.DecodeEvent(json));
        Assert.Null(decoded.Model);
        Assert.Null(decoded.CostUsd);
    }

    [Fact]
    public void A_dispatch_carrying_the_removed_budget_field_still_decodes()
    {
        // budget_usd left the contract with the dollar budget (2026-08-12). §10 is frozen, so
        // the removal is only safe if a peer still sending it is tolerated: an unmapped
        // property is ignored, not a decode failure that would strand every dispatch from an
        // older plane. This is the direction the round-trip tests cannot cover, since nothing
        // in the type can produce the field any more.
        var decoded = Assert.IsType<DispatchCommand>(RunnerWire.DecodeCommand(
            """
            {
              "type": "dispatch",
              "session": { "value": "3f2504e0-4f89-11d3-9a0c-0305e82c3301" },
              "profile": "default",
              "worker_token": "lbr_w_x",
              "budget_usd": 12.50
            }
            """));

        Assert.Equal("default", decoded.Profile);
        Assert.Equal("lbr_w_x", decoded.WorkerToken);
    }

    [Fact]
    public void Decode_command_rejects_unknown_type_and_malformed_json()
    {
        Assert.Null(RunnerWire.DecodeCommand("""{ "type": "frobnicate", "session": { "value": "x" } }"""));
        Assert.Null(RunnerWire.DecodeCommand("""{ "session": { "value": "x" } }"""));
        Assert.Null(RunnerWire.DecodeCommand("not json at all"));
        // An event on the command channel is not a command.
        Assert.Null(RunnerWire.DecodeCommand(RunnerWire.EncodeEvent(new StartedEvent(SessionId.New(), DateTimeOffset.UtcNow))));
    }

    [Fact]
    public void Decode_event_rejects_unknown_type_and_commands()
    {
        Assert.Null(RunnerWire.DecodeEvent("""{ "type": "frobnicate" }"""));
        Assert.Null(RunnerWire.DecodeEvent("not json at all"));
        // A command on the event channel is not an event.
        Assert.Null(RunnerWire.DecodeEvent(RunnerWire.EncodeCommand(new KillCommand(SessionId.New()))));
    }

    [Fact]
    public void Decode_heartbeat_rejects_non_heartbeat_envelopes()
    {
        Assert.Null(RunnerWire.DecodeHeartbeat(RunnerWire.EncodeEvent(new StartedEvent(SessionId.New(), DateTimeOffset.UtcNow))));
        Assert.Null(RunnerWire.DecodeHeartbeat("""{ "type": "kill", "session": { "value": "x" } }"""));
        Assert.Null(RunnerWire.DecodeHeartbeat("not json at all"));
    }

    /// <summary>
    /// The frozen §10 lists, spelled out as literals on purpose: adding a member here
    /// is the tripwire that puts a vocabulary change in front of a reviewer instead of
    /// letting it ride along in a feature diff.
    /// </summary>
    [Fact]
    public void Vocabulary_sets_are_the_closed_frozen_lists()
    {
        Assert.Equal(
            new HashSet<string> { "dispatch", "stop", "kill", "prompt", "open-forward", "close-forward", "read-transcript", "start-process", "stop-process", "write-process" },
            new HashSet<string>(RunnerWire.Commands));
        Assert.Equal(
            new HashSet<string> { "started", "session-started", "alive", "tool-call", "usage-reported", "subagent-spawned", "turn-ended", "exited", "auth-failed", "forward-opened", "forward-closed", "rebooted", "transcript-chunk", "process-started", "process-stopped", "process-written" },
            new HashSet<string>(RunnerWire.Events));
    }

    /// <summary>
    /// <c>prompt</c> round-trips (<c>ideas/sessions.md</c> stage 1) — the first command that
    /// assumes a worker is something you talk to rather than something you launch.
    ///
    /// <para>It names a task and nothing else, and the emptiness is the point: the input the
    /// worker is being woken for stays on the assignment and is pulled over the authenticated
    /// MCP call, so the read is a receipt (§11). A payload here would have been a message the
    /// plane could only report as <em>queued</em>.</para>
    /// </summary>
    [Fact]
    public void Prompt_command_round_trips_and_carries_no_message()
    {
        var task = SessionId.New();
        var encoded = RunnerWire.EncodeCommand(new PromptCommand(task));

        var decoded = Assert.IsType<PromptCommand>(RunnerWire.DecodeCommand(encoded, out _));
        Assert.Equal(task, decoded.Session);
        Assert.Equal(new PromptCommand(task), decoded);
    }

    /// <summary>
    /// <c>turn-ended</c> round-trips, stop reason and all. A null reason is its own case: an
    /// agent that ends a turn without saying why gets no reason invented for it (§2
    /// principle 2).
    /// </summary>
    [Theory]
    [InlineData("end_turn")]
    [InlineData("max_tokens")]
    [InlineData(null)]
    public void Turn_ended_event_round_trips(string? reason)
    {
        var task = SessionId.New();
        var at = DateTimeOffset.UtcNow;
        var encoded = RunnerWire.EncodeEvent(new TurnEndedEvent(task, reason, at));

        var decoded = Assert.IsType<TurnEndedEvent>(RunnerWire.DecodeEvent(encoded));
        Assert.Equal(task, decoded.Session);
        Assert.Equal(reason, decoded.StopReason);
    }
}
