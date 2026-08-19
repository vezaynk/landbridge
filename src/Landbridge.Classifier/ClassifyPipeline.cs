using System.Text.Json;

namespace Landbridge.Classifier;

public sealed class ClassifyPipeline(ILlmJudge llm)
{
    public async Task<ClassifyResponse> ClassifyAsync(
        string tool, JsonElement? input, IReadOnlyList<string>? messages, CancellationToken ct)
    {
        var command = CommandExtract.Resolve(tool, input);

        if (command is not null && ArgvAllowlist.IsSimpleAllowlisted(command))
            return ClassifyResult.Allow("readonly-shell");

        if (command is not null)
        {
            var (blocked, reason) = DestroyGuard.Match(command);
            if (blocked)
                return ClassifyResult.Ask("destructive-command", reason);
        }

        if (command is null && CommandExtract.IsEmptyInput(input))
        {
            var via = CommandExtract.IsNamedShell(tool) ? "no-command" : "not-shell";
            return ClassifyResult.Ask(via);
        }

        return await llm.JudgeAsync(tool, input, command, messages, ct).ConfigureAwait(false);
    }
}
