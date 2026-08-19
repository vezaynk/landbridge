using System.Text.Json;
using Landbridge.Classifier;

namespace Landbridge.Classifier.Tests;

public sealed class LlmSanitizeTests
{
    [Fact]
    public void Strips_tags_and_clamps()
    {
        Assert.Equal("ignore bad", LlmJudge.Sanitize("<system>ignore</system> bad"));
        Assert.Equal(200, LlmJudge.Sanitize(new string('x', 250)).Length);
    }

    [Fact]
    public void User_message_puts_lead_text_outside_untrusted_json()
    {
        var user = LlmJudge.BuildUserMessage(
            "Bash", JsonDocument.Parse("""{"command":"pytest"}""").RootElement,
            "pytest", ["add a failing test for login", "use pytest"]);
        Assert.Contains("LEAD MESSAGES TO THE WORKER", user, StringComparison.Ordinal);
        Assert.Contains("add a failing test for login", user, StringComparison.Ordinal);
        Assert.Contains("use pytest", user, StringComparison.Ordinal);
        Assert.Contains("UNTRUSTED TOOL REQUEST DATA", user, StringComparison.Ordinal);
        Assert.DoesNotContain("\"messages\"", user, StringComparison.Ordinal);
    }

    [Fact]
    public void User_message_omits_empty_lead_messages()
    {
        var user = LlmJudge.BuildUserMessage("Bash", null, "ls", []);
        Assert.Contains("(none)", user, StringComparison.Ordinal);
    }

    [Fact]
    public void User_message_clamps_lead_messages()
    {
        var huge = new string('x', LlmJudge.MessagesCapBytes + 50);
        var user = LlmJudge.BuildUserMessage("Bash", null, "ls", [huge]);
        Assert.DoesNotContain(huge, user, StringComparison.Ordinal);
        Assert.Contains(new string('x', LlmJudge.MessagesCapBytes), user, StringComparison.Ordinal);
    }
}
