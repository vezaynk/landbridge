using System.Text.Json.Serialization;

namespace Landbridge.ControlPlane;

/// <summary>
/// A dispatched worker's assignment (§7), returned by the <c>get_session</c> worker
/// tool — the first thing a worker reads (worker-skill.md). It carries the
/// server-assigned <c>namespace</c>, the Lead's prose <c>description</c> (the
/// whole brief), and the server-maintained <c>attempt</c> counter (so a
/// redispatched worker knows it may be inheriting a dirty directory, §7/§11).
///
/// <para>It is also the <b>delivery surface for an answered input request</b> (§11): a
/// worker that asked a question is gone by the time the answer lands, so the answer
/// waits here and reaches its successor on the successor's opening read. Deliberately
/// not through the resume argv — argv is world-readable via <c>ps</c> and
/// <c>/proc/&lt;pid&gt;/cmdline</c>, which is why §13 keeps enrollment tokens out of it
/// too — so the resume prompt stays generic config and the content comes over the
/// authenticated MCP call.</para>
///
/// The property names are pinned to snake_case with <see cref="JsonPropertyNameAttribute"/>
/// so the wire shape is stable regardless of the MCP serializer's naming policy —
/// the shape a worker harness parses. This is content the worker consumes, never
/// state the engine interprets.
/// </summary>
public sealed record WorkerAssignment(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("attempt")] int Attempt,
    // §10 in-band report: the report a prior attempt left, so a redispatched or
    // successor worker sees what the last attempt claimed it did (null on attempt 1
    // or when none was left). Opaque content, exactly like the description above.
    [property: JsonPropertyName("report")] string? Report = null,
    // §11 the answered input request, both halves. The question is here because after
    // a cold start (the machine holding the transcript was gone) the worker has no
    // memory of asking, and an answer with no question is unreadable. Both null until
    // this task asks something; the answer alone stays null while it is still waiting.
    [property: JsonPropertyName("question")] string? Question = null,
    [property: JsonPropertyName("answer")] string? Answer = null);

/// <summary>
/// Worker inbox: the assignment header plus any Lead envelope this pull delivered.
/// Replaces <c>get_session</c>. <see cref="Items"/> is non-empty only when this
/// call was the receipt for <c>awaiting_pull</c>.
/// </summary>
public sealed record WorkerInboxView(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("report")] string? Report,
    [property: JsonPropertyName("question")] string? Question,
    [property: JsonPropertyName("answer")] string? Answer,
    [property: JsonPropertyName("items")] IReadOnlyList<WorkerInboxItem> Items);

public sealed record WorkerInboxItem(
    [property: JsonPropertyName("messageId")] Guid? MessageId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string? Text);
