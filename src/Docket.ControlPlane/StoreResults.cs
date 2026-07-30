using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// Outcome of a store operation. Wraps the engine's <see cref="TransitionResult"/>
/// with the store-level outcomes the engine cannot express: the row was gone,
/// or an optimistic-concurrency race lost.
/// </summary>
public abstract record StoreResult
{
    private StoreResult() { }

    /// <summary>
    /// The transition committed. <paramref name="TraceContext"/> and
    /// <paramref name="HarnessSessionRef"/> are opaque transport metadata the store
    /// surfaces only where a caller needs it — the dispatch path reads
    /// <paramref name="TraceContext"/> to parent its span on the Lead's create_task
    /// trace, and <paramref name="HarnessSessionRef"/> to pass a prior session ref
    /// back to the runner for §11 resume. Null everywhere else; neither touches the
    /// engine (<see cref="TaskRecord"/> stays content-free).
    /// </summary>
    public sealed record Applied(
        TaskRecord Task,
        IReadOnlyList<Effect> Effects,
        string? TraceContext = null,
        string? HarnessSessionRef = null) : StoreResult;

    /// <summary>The engine refused the transition; nothing was written.</summary>
    public sealed record Rejected(Rule Rule, string Reason) : StoreResult;

    /// <summary>No task with that id (or, for dispatch, no eligible task).</summary>
    public sealed record NotFound(string Reason) : StoreResult;

    /// <summary>
    /// A concurrent writer moved the row first (xmin mismatch). The caller
    /// re-reads and retries — the transition was not applied.
    /// </summary>
    public sealed record Conflict(string Reason) : StoreResult;
}
