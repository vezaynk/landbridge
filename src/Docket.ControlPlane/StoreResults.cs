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
    /// <param name="BudgetCapUsd">
    /// §9.9: the per-dispatch cap committed against the Team for this dispatch, which the
    /// caller passes to the harness as <c>DispatchCommand.BudgetUsd</c> — the backstop that
    /// holds when spend telemetry is absent. Null when the Team configures no cap, and on
    /// every non-dispatch transition.
    /// </param>
    /// <param name="WorkDirTask">
    /// §11: the task whose machine-local work dir holds the session
    /// <paramref name="HarnessSessionRef"/> names, which the caller passes on as
    /// <c>DispatchCommand.WorkDirTask</c> — a continuation runs under a new task id, so
    /// without it the runner would look for the session in a directory that never held one.
    /// Null when that dir is the dispatched task's own, and on every non-dispatch transition.
    /// </param>
    public sealed record Applied(
        TaskRecord Task,
        IReadOnlyList<Effect> Effects,
        string? TraceContext = null,
        string? HarnessSessionRef = null,
        decimal? BudgetCapUsd = null,
        TaskId? WorkDirTask = null) : StoreResult;

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
