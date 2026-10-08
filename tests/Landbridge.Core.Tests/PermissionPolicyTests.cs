namespace Landbridge.Core.Tests;

public sealed class PermissionPolicyTests
{
    private static readonly SessionId Session = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    [Theory]
    [InlineData("mcp__landbridge__get_inbox")]
    [InlineData("mcp__landbridge__get_session")]
    [InlineData("landbridge__report_result")]
    [InlineData("landbridge_request_input")]
    [InlineData("get_inbox")]
    [InlineData("watch_inbox")]
    [InlineData("get_session")]
    [InlineData("start_process")]
    [InlineData("landbridge: get session")]
    [InlineData("landbridge: get_session")]
    [InlineData("Landbridge: Open Forward")]
    [InlineData("landbridge:report_result")]
    [InlineData("landbridge__stop_session")]
    [InlineData("landbridge__get_team_state")]
    [InlineData("mcp__landbridge__open_forward")]
    [InlineData("report_friction")]
    [InlineData("mcp__landbridge__report_friction")]
    [InlineData("create_team")]
    [InlineData("mcp__landbridge__create_team")]
    [InlineData("submit_plan")]
    [InlineData("mcp__landbridge__submit_plan")]
    [InlineData("mcp.landbridge.request_input")]
    [InlineData("mcp.landbridge.submit_plan")]
    [InlineData("mcp.landbridge.start_process")]
    [InlineData("mcp.landbridge.register_service")]
    [InlineData("MCP.Landbridge.Submit_Plan")]
    [InlineData("landbridge_get_lead_inbox")]
    [InlineData("landbridge_watch_lead_inbox")]
    [InlineData("landbridge_stop_session")]
    [InlineData("landbridge_list_profiles")]
    [InlineData("landbridge: submit plan · run pytest")]
    public void Protocol_and_runtime_tools_auto_allow(string tool)
    {
        Assert.Equal(PermissionDisposition.AutoAllow, PermissionPolicy.Classify(tool, "{}"));
    }

    [Theory]
    [InlineData("Execute `echo landbridge`")]
    [InlineData("Execute `cat landbridge-notes.md`")]
    [InlineData("Execute `echo mcp.landbridge is not a tool`")]
    [InlineData("Execute `cat landbridge_notes.md`")]
    [InlineData("Bash")]
    public void A_shell_title_that_merely_mentions_landbridge_still_asks(string tool)
    {
        Assert.Equal(PermissionDisposition.Ask, PermissionPolicy.Classify(tool, "{}"));
    }

    [Theory]
    [InlineData("tool", """{"name":"mcp__landbridge__get_inbox"}""")]
    [InlineData("execute", """{"server":"landbridge","tool":"submit_plan","arguments":{"plan":"run pytest"}}""")]
    [InlineData("tool", """{"server":"landbridge","tool":"start_process","arguments":{}}""")]
    [InlineData("execute", """{"server":"landbridge","tool":"register_service","arguments":{}}""")]
    [InlineData("use_tool", """{"tool_name":"landbridge__get_lead_inbox","tool_input":{"teamId":"t"}}""")]
    [InlineData("use_tool", """{"tool_name":"landbridge__watch_lead_inbox","tool_input":{}}""")]
    [InlineData("use_tool", """{"tool_name":"landbridge__stop_session","tool_input":{}}""")]
    public void Input_that_names_a_protocol_tool_auto_allows(string tool, string input)
    {
        Assert.Equal(PermissionDisposition.AutoAllow, PermissionPolicy.Classify(tool, input));
    }

    [Theory]
    [InlineData("Read", """{"path":"src/a.cs"}""")]
    [InlineData("Write", """{"path":"./notes.md","contents":"x"}""")]
    [InlineData("Edit", """{"file_path":"/work/aaaaaaaabbbbccccddddeeeeeeeeeeee/src/a.cs"}""")]
    [InlineData("Read", """{"path":"/work/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/README.md"}""")]
    public void Reads_and_writes_in_the_session_directory_auto_allow(string tool, string input)
    {
        Assert.Equal(PermissionDisposition.AutoAllow, PermissionPolicy.Classify(tool, input, Session));
    }

    [Theory]
    [InlineData("Read", """{"path":"/Users/me/.claude/skills"}""")]
    [InlineData("Write", """{"path":"../other/x"}""")]
    [InlineData("Read", """{"path":"/work/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/a.cs"}""")]
    [InlineData("Bash", """{"command":"cat src/a.cs"}""")]
    [InlineData("Bash", """{"command":"sudo rm -rf /"}""")]
    [InlineData("Bash", """{"command":"echo landbridge"}""")]
    [InlineData("use_tool", """{"tool_name":"github__create_issue","tool_input":{}}""")]
    public void Outside_the_session_directory_or_a_shell_still_asks(string tool, string input)
    {
        Assert.Equal(PermissionDisposition.Ask, PermissionPolicy.Classify(tool, input, Session));
    }
}
