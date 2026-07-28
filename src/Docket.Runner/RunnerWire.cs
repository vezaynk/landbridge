using System.Text.Json;

namespace Docket.Runner;

/// <summary>
/// The wire boundary for the frozen contract (§10). The real transport is
/// deferred, but the rule that defines the contract lives here: <b>a runner
/// rejects anything outside the vocabulary</b>. Decoding switches on a closed
/// set of <c>type</c> discriminators; an unrecognized command is refused
/// (returns <c>null</c>) rather than guessed at.
/// </summary>
public static class RunnerWire
{
    public const string Dispatch = "dispatch";
    public const string Stop = "stop";
    public const string Kill = "kill";
    public const string OpenForward = "open-forward";

    public const string Started = "started";
    public const string Alive = "alive";
    public const string ToolCall = "tool-call";
    public const string SubagentSpawned = "subagent-spawned";
    public const string Exited = "exited";
    public const string AuthFailed = "auth-failed";
    public const string ForwardClosed = "forward-closed";
    public const string Rebooted = "rebooted";

    /// <summary>The closed outbound (control plane → runner) vocabulary.</summary>
    public static IReadOnlySet<string> Commands { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Dispatch, Stop, Kill, OpenForward };

    /// <summary>The closed inbound (runner → control plane) vocabulary.</summary>
    public static IReadOnlySet<string> Events { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Started, Alive, ToolCall, SubagentSpawned, Exited, AuthFailed, ForwardClosed, Rebooted,
        };

    public static bool IsKnownCommand(string? type) => type is not null && Commands.Contains(type);

    public static bool IsKnownEvent(string? type) => type is not null && Events.Contains(type);

    /// <summary>
    /// Decodes one command from its JSON envelope, or returns <c>null</c> when
    /// the <c>type</c> is missing or outside the vocabulary — the rejection
    /// §10 requires. The concrete-type deserialize ignores the extra
    /// <c>type</c> discriminator property.
    /// </summary>
    public static RunnerCommand? DecodeCommand(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return typeElement.GetString() switch
            {
                Dispatch => doc.RootElement.Deserialize(RunnerJsonContext.Default.DispatchCommand),
                Stop => doc.RootElement.Deserialize(RunnerJsonContext.Default.StopCommand),
                Kill => doc.RootElement.Deserialize(RunnerJsonContext.Default.KillCommand),
                OpenForward => doc.RootElement.Deserialize(RunnerJsonContext.Default.OpenForwardCommand),
                _ => null, // §10: reject anything outside the vocabulary.
            };
        }
    }
}
