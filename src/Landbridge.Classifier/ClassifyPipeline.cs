using System.Text.Json;

namespace Landbridge.Classifier;

public sealed class ClassifyPipeline(ILlmJudge llm)
{
    public async Task<ClassifyResponse> ClassifyAsync(
        string tool, JsonElement? input, IReadOnlyList<string>? messages, CancellationToken ct)
    {
        var command = CommandExtract.Resolve(tool, input);

        // The only list here is a positive one. An allowlist that is missing an entry
        // costs an unnecessary question; a denylist that is missing an entry costs the
        // thing it was written to prevent, so this layer only ever says yes.
        if (command is not null && ArgvAllowlist.IsSimpleAllowlisted(command))
            return ClassifyResult.Allow("readonly-shell");

        if (command is null && CommandExtract.IsEmptyInput(input))
        {
            var via = CommandExtract.IsNamedShell(tool) ? "no-command" : "not-shell";
            return ClassifyResult.Ask(via);
        }

        return await llm.JudgeAsync(tool, input, command, messages, ct).ConfigureAwait(false);
    }
}
