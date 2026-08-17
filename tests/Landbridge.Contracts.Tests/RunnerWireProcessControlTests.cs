using Docket.Core;

namespace Docket.Contracts.Tests;

/// <summary>
/// The §10 process-control family on the wire: <c>start-process</c> /
/// <c>stop-process</c> / <c>write-process</c> and their three replies. The
/// vocabulary sets in <see cref="RunnerWireTests"/> already name these members,
/// but naming a discriminator and being able to carry the record are different
/// claims — these are the round-trips, plus the decode-side rejections that make
/// the vocabulary closed rather than merely documented.
/// </summary>
public class RunnerWireProcessControlTests
{
    // ── Commands ────────────────────────────────────────────────────────────

    [Fact]
    public void Start_process_command_round_trips_with_all_fields()
    {
        var original = new StartProcessCommand(
            SessionId.New(),
            "req-1",
            "devserver",
            Spawn: ["npm", "run", "dev", "--", "--port=3000"],
            WorkingDirectory: "/work/task-3/app",
            Env: new Dictionary<string, string> { ["NODE_ENV"] = "development", ["PORT"] = "3000" },
            OpenStdin: true);

        var decoded = Assert.IsType<StartProcessCommand>(
            RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        // Record value-equality compares the collection members by reference, so
        // the argv and the env are asserted on their contents.
        Assert.Equal(original.Session, decoded.Session);
        Assert.Equal(original.RequestId, decoded.RequestId);
        Assert.Equal(original.Name, decoded.Name);
        Assert.Equal(original.Spawn, decoded.Spawn);
        Assert.Equal(original.WorkingDirectory, decoded.WorkingDirectory);
        Assert.Equal(original.Env, decoded.Env);
        Assert.True(decoded.OpenStdin);
    }

    [Fact]
    public void Start_process_command_round_trips_with_only_the_required_fields()
    {
        var original = new StartProcessCommand(SessionId.New(), "req-1", "tailer", Spawn: ["tail", "-f", "log"]);

        var decoded = Assert.IsType<StartProcessCommand>(
            RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original.Spawn, decoded.Spawn);
        Assert.Null(decoded.WorkingDirectory);
        Assert.Null(decoded.Env);
        // A process whose stdin was never asked for must not arrive with a pipe
        // open — the default is closed on both sides of the wire.
        Assert.False(decoded.OpenStdin);
    }

    [Fact]
    public void Stop_process_command_round_trips()
    {
        var original = new StopProcessCommand(SessionId.New(), "req-2", "devserver");

        var decoded = Assert.IsType<StopProcessCommand>(
            RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Write_process_command_round_trips_verbatim_data()
    {
        // The payload is bytes for a pipe, not prose: quotes, backslashes, tabs,
        // newlines, and multi-byte characters all survive the envelope unchanged.
        var original = new WriteProcessCommand(
            SessionId.New(), "req-3", "repl",
            Data: "print(\"a \\\" quote\")\n\tindented, émoji 🛠\r\n",
            AppendNewline: false);

        var decoded = Assert.IsType<WriteProcessCommand>(
            RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.Equal(original.Data, decoded.Data);
        Assert.False(decoded.AppendNewline);
    }

    [Fact]
    public void Write_process_command_defaults_to_appending_a_newline()
    {
        var original = new WriteProcessCommand(SessionId.New(), "req-3", "repl", Data: "help");

        var decoded = Assert.IsType<WriteProcessCommand>(
            RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(original, decoded);
        Assert.True(decoded.AppendNewline);
    }

    [Fact]
    public void Write_process_command_round_trips_a_payload_at_the_in_band_cap()
    {
        // The 16 KB cap is a shared constant, so a payload right at it has to
        // survive the envelope; the cap is enforced above the wire, not by it.
        var data = new string('x', ProcessStdin.MaxBytes);
        var original = new WriteProcessCommand(SessionId.New(), "req-4", "repl", Data: data);

        var decoded = Assert.IsType<WriteProcessCommand>(
            RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original)));

        Assert.Equal(ProcessStdin.MaxBytes, decoded.Data.Length);
        Assert.Equal(data, decoded.Data);
    }

    // ── Events ──────────────────────────────────────────────────────────────

    [Fact]
    public void Process_started_event_round_trips_a_success_with_its_log_path()
    {
        var original = new ProcessStartedEvent(
            SessionId.New(), "req-1", "devserver", Started: true, LogPath: "/work/task-3/.docket/devserver.log");

        var decoded = Assert.IsType<ProcessStartedEvent>(
            RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        Assert.Equal(original, decoded);
        Assert.True(decoded.Started);
        Assert.Null(decoded.Refusal);
        Assert.Equal("/work/task-3/.docket/devserver.log", decoded.LogPath);
    }

    [Fact]
    public void Process_started_event_round_trips_each_refusal_in_the_closed_vocabulary()
    {
        // A refusal is the reply's payload, not an exception, so every member of
        // the §10 refusal vocabulary has to survive the envelope intact.
        foreach (var refusal in new[]
                 {
                     ProcessRefusals.ProfileNotPermitted,
                     ProcessRefusals.CapReached,
                     ProcessRefusals.InvalidName,
                     ProcessRefusals.InvalidSpawn,
                     ProcessRefusals.NameTaken,
                     ProcessRefusals.SpawnFailed,
                 })
        {
            var original = new ProcessStartedEvent(
                SessionId.New(), "req-1", "devserver", Started: false, Refusal: refusal);

            var decoded = Assert.IsType<ProcessStartedEvent>(
                RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

            Assert.Equal(original, decoded);
            Assert.False(decoded.Started);
            Assert.Equal(refusal, decoded.Refusal);
            Assert.Null(decoded.LogPath);
        }
    }

    [Fact]
    public void Process_stopped_event_round_trips_an_observed_exit_code()
    {
        var original = new ProcessStoppedEvent(
            SessionId.New(), "req-2", "devserver", Stopped: true, ExitCode: 143);

        var decoded = Assert.IsType<ProcessStoppedEvent>(
            RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        Assert.Equal(original, decoded);
        Assert.Equal(143, decoded.ExitCode);
        Assert.Null(decoded.Refusal);
    }

    [Fact]
    public void Process_stopped_event_distinguishes_no_observed_exit_code_from_zero()
    {
        // "The machine observed no code" and "it exited 0" are different facts;
        // a nullable that flattened to 0 on the wire would conflate them.
        var unobserved = new ProcessStoppedEvent(SessionId.New(), "req-2", "gone", Stopped: true);
        var exitedZero = new ProcessStoppedEvent(SessionId.New(), "req-2", "clean", Stopped: true, ExitCode: 0);

        var decodedUnobserved = Assert.IsType<ProcessStoppedEvent>(
            RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(unobserved)));
        var decodedZero = Assert.IsType<ProcessStoppedEvent>(
            RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(exitedZero)));

        Assert.Null(decodedUnobserved.ExitCode);
        Assert.Equal(0, decodedZero.ExitCode);
    }

    [Fact]
    public void Process_stopped_event_round_trips_a_refusal_for_an_unknown_name()
    {
        var original = new ProcessStoppedEvent(
            SessionId.New(), "req-2", "never-started", Stopped: false, Refusal: ProcessRefusals.NoSuchProcess);

        var decoded = Assert.IsType<ProcessStoppedEvent>(
            RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        Assert.Equal(original, decoded);
        Assert.False(decoded.Stopped);
        Assert.Equal(ProcessRefusals.NoSuchProcess, decoded.Refusal);
    }

    [Fact]
    public void Process_written_event_round_trips_the_accepted_byte_count()
    {
        var original = new ProcessWrittenEvent(
            SessionId.New(), "req-3", "repl", Written: true, Bytes: 4096);

        var decoded = Assert.IsType<ProcessWrittenEvent>(
            RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        Assert.Equal(original, decoded);
        Assert.True(decoded.Written);
        Assert.Equal(4096, decoded.Bytes);
        Assert.Null(decoded.Refusal);
    }

    [Fact]
    public void Process_written_event_round_trips_a_refusal_with_no_bytes_accepted()
    {
        var original = new ProcessWrittenEvent(
            SessionId.New(), "req-3", "repl", Written: false, Refusal: ProcessRefusals.StdinNotOpened);

        var decoded = Assert.IsType<ProcessWrittenEvent>(
            RunnerWire.DecodeEvent(RunnerWire.EncodeEvent(original)));

        Assert.Equal(original, decoded);
        Assert.False(decoded.Written);
        Assert.Equal(0, decoded.Bytes);
        Assert.Equal(ProcessRefusals.StdinNotOpened, decoded.Refusal);
    }

    // ── The vocabulary is closed on both channels (§10) ───────────────────────

    [Fact]
    public void Process_commands_and_events_are_recognized_on_their_own_channel_only()
    {
        foreach (var command in new[] { RunnerWire.StartProcess, RunnerWire.StopProcess, RunnerWire.WriteProcess })
        {
            Assert.True(RunnerWire.IsKnownCommand(command));
            Assert.False(RunnerWire.IsKnownEvent(command));
        }

        foreach (var evt in new[] { RunnerWire.ProcessStarted, RunnerWire.ProcessStopped, RunnerWire.ProcessWritten })
        {
            Assert.True(RunnerWire.IsKnownEvent(evt));
            Assert.False(RunnerWire.IsKnownCommand(evt));
        }
    }

    [Fact]
    public void Neither_channel_recognizes_an_absent_or_unknown_discriminator()
    {
        Assert.False(RunnerWire.IsKnownCommand(null));
        Assert.False(RunnerWire.IsKnownEvent(null));
        Assert.False(RunnerWire.IsKnownCommand("frobnicate"));
        Assert.False(RunnerWire.IsKnownEvent("frobnicate"));
        // Case matters: the sets compare ordinally, so a near-miss is a miss.
        Assert.False(RunnerWire.IsKnownCommand("Start-Process"));
        Assert.False(RunnerWire.IsKnownEvent("Process-Started"));
    }

    [Fact]
    public void A_process_reply_is_not_accepted_as_a_command_and_vice_versa()
    {
        var reply = RunnerWire.EncodeEvent(
            new ProcessStartedEvent(SessionId.New(), "req-1", "devserver", Started: true));
        var command = RunnerWire.EncodeCommand(
            new StartProcessCommand(SessionId.New(), "req-1", "devserver", Spawn: ["true"]));

        Assert.Null(RunnerWire.DecodeCommand(reply));
        Assert.Null(RunnerWire.DecodeEvent(command));
        // Neither is a heartbeat.
        Assert.Null(RunnerWire.DecodeHeartbeat(reply));
        Assert.Null(RunnerWire.DecodeHeartbeat(command));
    }

    /// <summary>
    /// The decode side rejects an envelope whose <c>type</c> is absent or is not a
    /// string, rather than treating a typeless object as some default member. The
    /// existing rejection tests cover an unknown-but-string discriminator and
    /// outright malformed JSON; these are the shapes in between.
    /// </summary>
    [Fact]
    public void Decode_rejects_an_envelope_whose_type_is_missing_or_not_a_string()
    {
        foreach (var json in new[]
                 {
                     """{ "request_id": "req-1", "name": "devserver" }""",   // no discriminator at all
                     """{ "type": 7, "name": "devserver" }""",                // number, not a string
                     """{ "type": null, "name": "devserver" }""",             // explicit null
                     """{ "type": ["start-process"] }""",                     // array, not a string
                     """{ "type": { "value": "start-process" } }""",          // object, not a string
                     """[ { "type": "start-process" } ]""",                   // root is not an object
                     """ "start-process" """,                                 // root is a bare string
                 })
        {
            Assert.Null(RunnerWire.DecodeCommand(json));
            Assert.Null(RunnerWire.DecodeEvent(json));
            Assert.Null(RunnerWire.DecodeHeartbeat(json));
        }
    }

    [Fact]
    public void A_process_command_envelope_carries_its_traceparent_like_any_other()
    {
        // §1 tracing is envelope metadata, so it rides the process family too and
        // leaves the domain record untouched.
        const string traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var original = new StartProcessCommand(SessionId.New(), "req-1", "devserver", Spawn: ["true"]);

        var decoded = Assert.IsType<StartProcessCommand>(
            RunnerWire.DecodeCommand(RunnerWire.EncodeCommand(original, traceparent), out var decodedTp));

        Assert.Equal(traceparent, decodedTp);
        Assert.Equal(original.Name, decoded.Name);
        Assert.Equal(original.Spawn, decoded.Spawn);
    }
}
