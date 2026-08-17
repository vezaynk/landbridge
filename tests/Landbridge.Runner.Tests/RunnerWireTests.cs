using Docket.Core;

namespace Docket.Runner.Tests;

/// <summary>The frozen contract's wire boundary, spec §10: a runner rejects
/// anything outside the vocabulary.</summary>
public class RunnerWireTests
{
    [Fact]
    public void Decodes_a_known_kill_command()
    {
        var id = SessionId.New();
        var json = $$"""{ "type": "kill", "session": { "value": "{{id.Value}}" } }""";

        var command = RunnerWire.DecodeCommand(json);

        var kill = Assert.IsType<KillCommand>(command);
        Assert.Equal(id, kill.Session);
    }

    [Fact]
    public void Decodes_a_known_dispatch_command()
    {
        var id = SessionId.New();
        var json = $$"""{ "type": "dispatch", "session": { "value": "{{id.Value}}" }, "profile": "restricted" }""";

        var command = RunnerWire.DecodeCommand(json);

        var dispatch = Assert.IsType<DispatchCommand>(command);
        Assert.Equal(id, dispatch.Session);
        Assert.Equal("restricted", dispatch.Profile);
    }

    [Fact]
    public void Rejects_a_command_outside_the_vocabulary()
    {
        var json = """{ "type": "frobnicate", "session": { "value": "00000000-0000-0000-0000-000000000000" } }""";

        // §10: outside the closed vocabulary → refused, not guessed at.
        Assert.Null(RunnerWire.DecodeCommand(json));
        Assert.False(RunnerWire.IsKnownCommand("frobnicate"));
    }

    [Fact]
    public void Rejects_an_envelope_with_no_type()
    {
        Assert.Null(RunnerWire.DecodeCommand("""{ "session": { "value": "x" } }"""));
        Assert.Null(RunnerWire.DecodeCommand("not json at all"));
    }

    [Fact]
    public void Decodes_a_known_read_transcript_command()
    {
        // §12 serving: the runner side of the pull. An envelope naming only the required
        // fields reads as an inventory request for the task.
        var id = SessionId.New();
        var json = $$"""
            { "type": "read-transcript", "session": { "value": "{{id.Value}}" }, "request_id": "req-1" }
            """;

        var read = Assert.IsType<ReadTranscriptCommand>(RunnerWire.DecodeCommand(json));

        Assert.Equal(id, read.Session);
        Assert.Equal("req-1", read.RequestId);
        Assert.Equal(0, read.Ordinal);
        Assert.Equal(TranscriptStreams.Stdout, read.Stream);
    }

    [Fact]
    public void Decodes_a_known_close_forward_command()
    {
        // §8.3: the runner side of ending a splice whose owning task left working. The
        // envelope names the forward; the task rides along as §10 correlation.
        var id = SessionId.New();
        var json = $$"""
            { "type": "close-forward", "session": { "value": "{{id.Value}}" }, "forward_id": "fwd-9" }
            """;

        var close = Assert.IsType<CloseForwardCommand>(RunnerWire.DecodeCommand(json));

        Assert.Equal(id, close.Session);
        Assert.Equal("fwd-9", close.ForwardId);
    }

    /// <summary>The frozen §10 lists, spelled out as literals on purpose — the same
    /// tripwire as <c>Docket.Contracts.Tests</c>, asserted here too because this is the
    /// side that must reject anything outside the vocabulary.</summary>
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
}
