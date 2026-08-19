using System.Text.Json;
using Landbridge.Classifier;

namespace Landbridge.Classifier.Tests;

public sealed class ClassifyPipelineTests
{
    private static ClassifyPipeline Pipeline(ILlmJudge? llm = null) =>
        new(llm ?? new RecordingJudge());

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Git_status_allows_without_llm()
    {
        var llm = new RecordingJudge();
        var r = await Pipeline(llm).ClassifyAsync("Bash", El("""{"command":"git status"}"""), null, default);
        Assert.Equal("allow", r.Disposition);
        Assert.Equal("readonly-shell", r.Via);
        Assert.False(llm.Called);
    }

    [Fact]
    public async Task Title_git_version_allows()
    {
        var r = await Pipeline().ClassifyAsync("git --version", El("{}"), null, default);
        Assert.Equal("allow", r.Disposition);
        Assert.Equal("readonly-shell", r.Via);
    }

    [Fact]
    public async Task Pipe_or_true_does_not_short_circuit()
    {
        var llm = new RecordingJudge { Reply = ClassifyResult.Allow("classifier-fast") };
        var r = await Pipeline(llm).ClassifyAsync(
            "Bash", El("""{"command":"git --version || true"}"""), null, default);
        Assert.True(llm.Called);
        Assert.Equal("allow", r.Disposition);
        Assert.Equal("classifier-fast", r.Via);
    }

    [Fact]
    public async Task Destroy_guard_asks_without_llm()
    {
        var llm = new RecordingJudge();
        var r = await Pipeline(llm).ClassifyAsync(
            "Bash", El("""{"command":"git reset --hard"}"""), null, default);
        Assert.False(llm.Called);
        Assert.Equal("ask", r.Disposition);
        Assert.Equal("destructive-command", r.Via);
    }

    [Fact]
    public async Task Empty_execute_asks_without_llm()
    {
        var llm = new RecordingJudge();
        var r = await Pipeline(llm).ClassifyAsync("Bash", El("{}"), null, default);
        Assert.False(llm.Called);
        Assert.Equal("ask", r.Disposition);
        Assert.Equal("no-command", r.Via);
    }

    [Fact]
    public async Task Write_goes_to_llm()
    {
        var llm = new RecordingJudge { Reply = ClassifyResult.Ask("classifier-block", "writes a file") };
        var r = await Pipeline(llm).ClassifyAsync(
            "Bash", El("""{"command":"printf hi > f.txt"}"""), null, default);
        Assert.True(llm.Called);
        Assert.Equal("ask", r.Disposition);
        Assert.Equal("classifier-block", r.Via);
    }

    [Fact]
    public async Task Llm_path_forwards_lead_messages()
    {
        var llm = new RecordingJudge { Reply = ClassifyResult.Allow("classifier-fast") };
        var messages = new[] { "add a failing test for login", "use pytest" };
        var r = await Pipeline(llm).ClassifyAsync(
            "Bash", El("""{"command":"pytest"}"""), messages, default);
        Assert.True(llm.Called);
        Assert.Equal(messages, llm.Messages);
        Assert.Equal("allow", r.Disposition);
    }

    [Fact]
    public async Task Readonly_shell_skips_llm_even_with_messages()
    {
        var llm = new RecordingJudge();
        var r = await Pipeline(llm).ClassifyAsync(
            "Bash", El("""{"command":"git status"}"""),
            ["run the tests"], default);
        Assert.False(llm.Called);
        Assert.Equal("readonly-shell", r.Via);
    }

    [Fact]
    public async Task Grok_execute_title_unwraps()
    {
        var r = await Pipeline().ClassifyAsync("Execute `git status`", El("{}"), null, default);
        Assert.Equal("allow", r.Disposition);
        Assert.Equal("readonly-shell", r.Via);
    }

    private sealed class RecordingJudge : ILlmJudge
    {
        public bool Called { get; private set; }
        public IReadOnlyList<string>? Messages { get; private set; }
        public ClassifyResponse Reply { get; set; } = ClassifyResult.Ask("classifier-unavailable");

        public Task<ClassifyResponse> JudgeAsync(
            string tool, JsonElement? input, string? command,
            IReadOnlyList<string>? messages, CancellationToken ct)
        {
            Called = true;
            Messages = messages;
            return Task.FromResult(Reply);
        }
    }
}
