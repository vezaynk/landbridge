namespace Landbridge.MultiMachine.Tests;

public sealed class AcpSessionIdExtractionTests
{
    [Fact]
    public void Prefers_session_new_result_over_initialize_and_session_update()
    {
        var profile = RealHarnessProfiles.Claude("/bin/true");
        var lines = new[]
        {
            """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"agentCapabilities":{},"sessionId":"init-id"}}""",
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"update-id","update":{"sessionUpdate":"agent_message_chunk"}}}""",
            """{"jsonrpc":"2.0","id":2,"result":{"sessionId":"new-id"}}""",
        };
        Assert.Equal("new-id", profile.SessionIdFromTranscript(lines));
    }

    [Fact]
    public void Falls_back_to_session_update_when_session_new_has_no_id()
    {
        var profile = RealHarnessProfiles.Claude("/bin/true");
        var lines = new[]
        {
            """{"jsonrpc":"2.0","id":2,"result":{"configOptions":[]}}""",
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"loaded-id","update":{}}}""",
        };
        Assert.Equal("loaded-id", profile.SessionIdFromTranscript(lines));
    }

    [Fact]
    public void Prefers_the_first_session_new_result_matching_what_the_client_stamps()
    {
        var profile = RealHarnessProfiles.Claude("/bin/true");
        var lines = new[]
        {
            """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"agentCapabilities":{},"sessionId":"init-id"}}""",
            """{"jsonrpc":"2.0","id":2,"result":{"sessionId":"stamped-id","modes":{},"configOptions":[]}}""",
            """{"jsonrpc":"2.0","id":3,"result":{"sessionId":"later-id"}}""",
        };
        Assert.Equal("stamped-id", profile.SessionIdFromTranscript(lines));
    }

    [Fact]
    public void Session_new_with_agent_capabilities_is_still_a_session_new()
    {
        var profile = RealHarnessProfiles.Claude("/bin/true");
        var lines = new[]
        {
            """{"jsonrpc":"2.0","id":2,"result":{"sessionId":"new-id","agentCapabilities":{}}}""",
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"update-id","update":{}}}""",
        };
        Assert.Equal("new-id", profile.SessionIdFromTranscript(lines));
    }
}
