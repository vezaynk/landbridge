namespace Landbridge.Core.Tests;

public sealed class PermissionPolicyTests
{
    private static readonly SessionId Session = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    [Theory]
    [InlineData("mcp__landbridge__get_session")]
    [InlineData("landbridge__report_result")]
    [InlineData("landbridge_request_input")]
    [InlineData("get_session")]
    [InlineData("start_process")]
    public void Protocol_and_runtime_tools_auto_allow(string tool)
    {
        Assert.Equal(PermissionDisposition.AutoAllow, PermissionPolicy.Classify(tool, "{}"));
    }

    [Fact]
    public void Input_that_names_a_protocol_tool_auto_allows()
    {
        Assert.Equal(
            PermissionDisposition.AutoAllow,
            PermissionPolicy.Classify("tool", """{"name":"mcp__landbridge__get_session"}"""));
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
    public void Outside_the_session_directory_or_a_shell_still_asks(string tool, string input)
    {
        Assert.Equal(PermissionDisposition.Ask, PermissionPolicy.Classify(tool, input, Session));
    }
}
