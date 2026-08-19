namespace Landbridge.Core.Tests;

public sealed class PermissionPolicyTests
{
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
    [InlineData("Bash", """{"command":"sudo rm -rf /"}""")]
    [InlineData("Bash", """{"command":"cat ~/.ssh/id_rsa"}""")]
    [InlineData("Read", """{"path":"/Users/me/.claude/skills"}""")]
    public void Credential_and_home_paths_auto_deny(string tool, string input)
    {
        Assert.Equal(PermissionDisposition.AutoDeny, PermissionPolicy.Classify(tool, input));
    }

    [Fact]
    public void Ordinary_execute_still_asks()
    {
        Assert.Equal(
            PermissionDisposition.Ask,
            PermissionPolicy.Classify("Bash", """{"command":"git ls-remote origin"}"""));
    }
}
