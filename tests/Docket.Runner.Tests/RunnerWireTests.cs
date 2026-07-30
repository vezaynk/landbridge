using Docket.Core;

namespace Docket.Runner.Tests;

/// <summary>The frozen contract's wire boundary, spec §10: a runner rejects
/// anything outside the vocabulary.</summary>
public class RunnerWireTests
{
    [Fact]
    public void Decodes_a_known_kill_command()
    {
        var id = TaskId.New();
        var json = $$"""{ "type": "kill", "task": { "value": "{{id.Value}}" } }""";

        var command = RunnerWire.DecodeCommand(json);

        var kill = Assert.IsType<KillCommand>(command);
        Assert.Equal(id, kill.Task);
    }

    [Fact]
    public void Decodes_a_known_dispatch_command()
    {
        var id = TaskId.New();
        var json = $$"""{ "type": "dispatch", "task": { "value": "{{id.Value}}" }, "profile": "restricted" }""";

        var command = RunnerWire.DecodeCommand(json);

        var dispatch = Assert.IsType<DispatchCommand>(command);
        Assert.Equal(id, dispatch.Task);
        Assert.Equal("restricted", dispatch.Profile);
    }

    [Fact]
    public void Rejects_a_command_outside_the_vocabulary()
    {
        var json = """{ "type": "frobnicate", "task": { "value": "00000000-0000-0000-0000-000000000000" } }""";

        // §10: outside the closed vocabulary → refused, not guessed at.
        Assert.Null(RunnerWire.DecodeCommand(json));
        Assert.False(RunnerWire.IsKnownCommand("frobnicate"));
    }

    [Fact]
    public void Rejects_an_envelope_with_no_type()
    {
        Assert.Null(RunnerWire.DecodeCommand("""{ "task": { "value": "x" } }"""));
        Assert.Null(RunnerWire.DecodeCommand("not json at all"));
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
