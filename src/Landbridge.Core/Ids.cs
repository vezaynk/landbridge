namespace Landbridge.Core;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct TeamId(Guid Value)
{
    public static TeamId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// One dispatch of one task. Spec §5: each dispatch mints a distinct worker
/// instance; requeue/redispatch revokes the predecessor, and worker-triggered
/// transitions are accepted only from the incumbent (§9 check 14).
/// </summary>
public readonly record struct WorkerInstanceId(Guid Value)
{
    public static WorkerInstanceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
