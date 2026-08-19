using System.Text.Json;

namespace Landbridge.Classifier;

public interface ILlmJudge
{
    Task<ClassifyResponse> JudgeAsync(
        string tool, JsonElement? input, string? command,
        IReadOnlyList<string>? messages, CancellationToken ct);
}
