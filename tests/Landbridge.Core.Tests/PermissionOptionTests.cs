using Landbridge.Core;

namespace Landbridge.Core.Tests;

public sealed class PermissionOptionTests
{
    private const string Offered = """
        [
          {"optionId":"allow-once","name":"Allow once","kind":"allow_once"},
          {"optionId":"allow-always","name":"Always allow","kind":"allow_always"},
          {"optionId":"reject-once","name":"Reject","kind":"reject_once"}
        ]
        """;

    [Fact]
    public void Parse_keeps_id_name_and_kind()
    {
        var options = PermissionOption.Parse(Offered);
        Assert.Equal(3, options.Count);
        Assert.Equal("allow-once", options[0].OptionId);
        Assert.Equal("Allow once", options[0].Name);
        Assert.Equal("allow_once", options[0].Kind);
        Assert.Equal(PermissionVerdict.Allow, options[0].Verdict);
        Assert.Equal(PermissionVerdict.Deny, options[2].Verdict);
    }

    [Theory]
    [InlineData("allow-once", "allow-once")]
    [InlineData("allow_once", "allow-once")]
    [InlineData("allow", "allow-once")]
    [InlineData("allow-always", "allow-always")]
    [InlineData("deny", "reject-once")]
    [InlineData("reject_once", "reject-once")]
    public void Resolve_matches_option_id_kind_and_aliases(string choice, string expectedId)
    {
        var match = PermissionOption.Resolve(PermissionOption.Parse(Offered), choice);
        Assert.NotNull(match);
        Assert.Equal(expectedId, match.OptionId);
    }

    [Fact]
    public void Resolve_rejects_unknown_choice()
    {
        Assert.Null(PermissionOption.Resolve(PermissionOption.Parse(Offered), "maybe"));
        Assert.Null(PermissionOption.Resolve([], "allow"));
        Assert.Empty(PermissionOption.Parse("not json"));
    }
}
