using Landbridge.Classifier;

namespace Landbridge.Classifier.Tests;

public sealed class LlmJudgeTests
{
    [Theory]
    [InlineData("""{"shouldBlock":false}""", false)]
    [InlineData("""{"shouldBlock":true,"reason":"rm -rf"}""", true)]
    public void Parse_reads_an_explicit_boolean(string json, bool shouldBlock)
    {
        var parsed = LlmJudge.ParseJudgeJson(json);
        Assert.Equal(shouldBlock, parsed.ShouldBlock);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{"reason":"no flag"}""")]
    [InlineData("```json\n{\"shouldBlock\":true}\n```")]
    public void Parse_miss_is_a_missing_decision(string? json)
    {
        var parsed = LlmJudge.ParseJudgeJson(json);
        Assert.Null(parsed.ShouldBlock);
    }

    [Fact]
    public void Fast_allow_does_not_need_review()
    {
        var got = LlmJudge.CombineStages(
            new LlmJudge.JudgeJson(false, null),
            new LlmJudge.JudgeJson(true, "unused"));
        Assert.Equal("allow", got.Disposition);
        Assert.Equal("classifier-fast", got.Via);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void Fast_block_or_parse_miss_is_reviewed(bool? stage1)
    {
        var allow = LlmJudge.CombineStages(
            new LlmJudge.JudgeJson(stage1, null),
            new LlmJudge.JudgeJson(false, null));
        Assert.Equal("allow", allow.Disposition);
        Assert.Equal("classifier-review", allow.Via);

        var block = LlmJudge.CombineStages(
            new LlmJudge.JudgeJson(stage1, null),
            new LlmJudge.JudgeJson(true, "writes /etc/passwd"));
        Assert.Equal("ask", block.Disposition);
        Assert.Equal("classifier-block", block.Via);
        Assert.Equal("writes /etc/passwd", block.Reason);
    }

    [Fact]
    public void Review_parse_miss_is_unavailable()
    {
        var got = LlmJudge.CombineStages(
            new LlmJudge.JudgeJson(true, null),
            new LlmJudge.JudgeJson(null, null));
        Assert.Equal("ask", got.Disposition);
        Assert.Equal("classifier-unavailable", got.Via);
    }
}
