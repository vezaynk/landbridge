using System.Text.Json;
using System.Text.Json.Serialization;

namespace Docket.Verifier;

/// <summary>
/// Source-generated JSON metadata for the verifier webhook's wire shapes,
/// keeping the console AOT-clean (like <c>docketd</c>'s config context). The
/// plane serialises with ASP.NET's Web defaults (camelCase), so this context
/// uses the same policy: <c>taskId</c>, <c>completionCriteria</c>,
/// <c>resultReference</c>, <c>verdict</c>, <c>state</c>, <c>rule</c>,
/// <c>reason</c>. Enum-valued fields (<c>state</c>) arrive as plain strings and
/// are read as such — no converter needed.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<PendingTask>))]
[JsonSerializable(typeof(VerdictBody))]
[JsonSerializable(typeof(VerdictResponseBody))]
internal sealed partial class VerifierJsonContext : JsonSerializerContext;

/// <summary>
/// One task awaiting an automated verdict, as GET <c>/verify/pending</c> returns
/// it (the plane's <c>VerifyingTaskView</c>). <see cref="CompletionCriteria"/>
/// and <see cref="ResultReference"/> are opaque to the verifier — it interprets
/// them by its own policy (§7), never the plane's. <see cref="ResultReference"/>
/// is nullable only defensively; the plane's state machine forbids reaching
/// <c>verifying</c> without one (§6).
/// </summary>
public sealed record PendingTask(
    Guid TaskId,
    string Namespace,
    string CompletionCriteria,
    string? ResultReference);

/// <summary>The POST <c>/verify/{taskId}</c> body: a single verdict string (§10).</summary>
internal sealed record VerdictBody(string Verdict);

/// <summary>
/// The webhook's verdict response, across its status codes: 200 carries
/// <see cref="State"/>; 409 carries <see cref="Rule"/> + <see cref="Reason"/>
/// (or just <see cref="Reason"/> on a concurrency conflict); 404/400 carry
/// <see cref="Reason"/>. All optional so one shape reads every outcome.
/// </summary>
internal sealed record VerdictResponseBody(string? State, string? Rule, string? Reason);
