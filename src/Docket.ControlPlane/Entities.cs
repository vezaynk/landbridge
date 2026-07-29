using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// The persisted task row. Carries the typed state-machine fields plus the
/// opaque blobs the control plane stores and never interprets (§2 principle 1,
/// §7). Only <see cref="TaskStore"/> writes it, and only by running a
/// transition through <see cref="TaskStateMachine"/>.
/// </summary>
public sealed class TaskRow
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string Namespace { get; set; } = "";
    public CompletionMode CompletionMode { get; set; }
    public TaskState State { get; set; }
    public string? Profile { get; set; }

    public int Attempt { get; set; }
    public int InfrastructureRequeues { get; set; }
    public int VerificationFailures { get; set; }
    public int VerificationRetryLimit { get; set; }

    public Guid? CurrentInstanceId { get; set; }

    // Park record (§11); null unless the task has parked.
    public string? ParkMachine { get; set; }
    public string? ParkDirectory { get; set; }
    public string? ParkSessionRef { get; set; }
    public int? ParkAttempt { get; set; }

    // Opaque to the control plane: stored, returned, never dereferenced (§7).
    public string CompletionCriteria { get; set; } = "";

    /// <summary>
    /// The Lead's prose instructions (§7 <c>description</c>). Opaque: the worker
    /// reads it (worker-skill.md), the control plane never parses it. Captured at
    /// creation and handed back by <c>get_task</c>.
    /// </summary>
    public string Description { get; set; } = "";

    public string? Workspace { get; set; }
    public string? ResultReference { get; set; }

    /// <summary>
    /// The ambient W3C trace context (traceparent) captured when the Lead created
    /// the task. Opaque transport metadata, exactly like <see cref="ResultReference"/>:
    /// stored verbatim, never dereferenced by the control plane, never entering
    /// <c>Docket.Core</c>. Dispatch continues the Lead's trace from here so one
    /// trace spans create_task → dispatch → runner → worker. Null when no Activity
    /// was sampling at creation.
    /// </summary>
    public string? TraceContext { get; set; }

    /// <summary>Postgres system column, used as the optimistic-concurrency token.</summary>
    public uint Version { get; set; }

    internal TaskRecord ToDomain() => new()
    {
        Id = new TaskId(Id),
        Team = new TeamId(TeamId),
        Namespace = Namespace,
        CompletionMode = CompletionMode,
        State = State,
        Profile = Profile,
        Attempt = Attempt,
        InfrastructureRequeues = InfrastructureRequeues,
        VerificationFailures = VerificationFailures,
        VerificationRetryLimit = VerificationRetryLimit,
        CurrentInstance = CurrentInstanceId is { } i ? new WorkerInstanceId(i) : null,
        Park = ParkMachine is { } m
            ? new ParkRecord(m, ParkDirectory!, ParkSessionRef!, ParkAttempt!.Value)
            : null,
    };

    internal void CopyFrom(TaskRecord task)
    {
        State = task.State;
        Attempt = task.Attempt;
        InfrastructureRequeues = task.InfrastructureRequeues;
        VerificationFailures = task.VerificationFailures;
        CurrentInstanceId = task.CurrentInstance?.Value;
        ParkMachine = task.Park?.Machine;
        ParkDirectory = task.Park?.Directory;
        ParkSessionRef = task.Park?.HarnessSessionRef;
        ParkAttempt = task.Park?.Attempt;
    }
}

/// <summary>
/// The token registry that makes §9 check 14 enforceable at the store: one
/// row per dispatch, revoked when the instance stops being incumbent. A
/// zombie's token is a revoked row.
/// </summary>
public sealed class WorkerInstanceRow
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// A registered live endpoint (§8.2). Rows for a task are cleared when it
/// leaves <see cref="TaskState.Working"/> (the ClearServicesAndForwards
/// effect).
/// </summary>
public sealed class RegisteredServiceRow
{
    public long Seq { get; set; }
    public Guid TaskId { get; set; }
    public Guid TeamId { get; set; }
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Append-only transition journal. Monotonic <see cref="Seq"/> gives the
/// per-recipient ordering the messaging layer will build on; for now it is
/// the store's own audit trail and the NOTIFY trigger's payload source.
/// </summary>
public sealed class TaskEventRow
{
    public long Seq { get; set; }
    public Guid TaskId { get; set; }
    public Guid TeamId { get; set; }
    public string Kind { get; set; } = "";
    public TaskState? FromState { get; set; }
    public TaskState? ToState { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
