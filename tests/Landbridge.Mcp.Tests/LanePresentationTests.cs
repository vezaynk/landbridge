using Landbridge.ControlPlane;
using Landbridge.Core;
using Landbridge.Mcp.Dashboard.Observability;

namespace Landbridge.Mcp.Tests;

public sealed class LanePresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Occupancy_chip_is_lost_connection_when_there_is_no_heartbeat()
    {
        var lane = Lane(state: SessionState.Working, heartbeat: null);
        var meta = LaneMeta.Of(lane, Now);
        Assert.Equal("lost connection", meta.Label);
        Assert.False(meta.Live);
        Assert.Equal("last heartbeat —", LaneNow.Text(lane, Now));
    }

    [Fact]
    public void Occupancy_chip_is_lost_connection_when_the_heartbeat_is_stale()
    {
        var lane = Lane(state: SessionState.Working, heartbeat: Now - TimeSpan.FromSeconds(20), machineLive: true);
        Assert.Equal("lost connection", LaneMeta.Of(lane, Now).Label);
        Assert.Equal("last heartbeat 20s ago", LaneNow.Text(lane, Now));
    }

    [Fact]
    public void Right_now_uses_the_envelope_when_the_heartbeat_is_fresh()
    {
        var lane = Lane(
            state: SessionState.Working,
            heartbeat: Now - TimeSpan.FromSeconds(2),
            machineLive: true,
            message: MessageState.AwaitingPull);
        Assert.Equal("awaiting_pull", LaneNow.Text(lane, Now));
        Assert.True(LaneMeta.Of(lane, Now).Live);
        Assert.Equal("working", LaneMeta.Of(lane, Now).Label);
    }

    [Fact]
    public void Right_now_ignores_the_last_occupancy_ToState()
    {
        var lane = Lane(
            state: SessionState.Working,
            heartbeat: Now - TimeSpan.FromSeconds(2),
            machineLive: true,
            message: MessageState.Idle,
            lastProgress: Now - TimeSpan.FromSeconds(4),
            tail: [new ObservabilityTailLine(Now, nameof(Dispatch), "Working")]);
        Assert.Equal("progress 4s ago", LaneNow.Text(lane, Now));
    }

    [Fact]
    public void Right_now_keeps_the_envelope_ahead_of_progress_when_someone_owes_a_turn()
    {
        var lane = Lane(
            state: SessionState.Working,
            heartbeat: Now - TimeSpan.FromSeconds(2),
            machineLive: true,
            message: MessageState.AwaitingPull,
            lastProgress: Now - TimeSpan.FromSeconds(1));
        Assert.Equal("awaiting_pull", LaneNow.Text(lane, Now));
    }

    [Fact]
    public void Right_now_keeps_a_permission_prompt_when_the_box_is_still_heartbeating()
    {
        var lane = Lane(
            state: SessionState.Working,
            heartbeat: Now - TimeSpan.FromSeconds(1),
            machineLive: true,
            inputKind: InputRequestKind.Permission,
            permissionTool: "Bash",
            blockedAt: Now);
        Assert.Equal("permission: Bash", LaneNow.Text(lane, Now));
        Assert.Equal("permission", LaneMeta.Of(lane, Now).Label);
    }

    [Fact]
    public void Failed_still_names_the_requeue_reason()
    {
        var lane = Lane(
            state: SessionState.Failed,
            heartbeat: null,
            lastRequeue: LivenessLossReason.TurnEndedWithoutResult);
        Assert.Equal("TurnEndedWithoutResult", LaneNow.Text(lane, Now));
        Assert.Equal("failed", LaneMeta.Of(lane, Now).Label);
    }

    private static ObservabilityLane Lane(
        SessionState state,
        DateTimeOffset? heartbeat,
        bool machineLive = false,
        MessageState message = MessageState.Idle,
        InputRequestKind? inputKind = null,
        string? permissionTool = null,
        DateTimeOffset? blockedAt = null,
        LivenessLossReason? lastRequeue = null,
        DateTimeOffset? lastProgress = null,
        IReadOnlyList<ObservabilityTailLine>? tail = null) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), "ns", "codex-apphost-linux",
            state, message, Attempt: 1, ReportUnread: false,
            OpenedAt: Now, blockedAt, inputKind,
            Question: null, Answer: null, permissionTool, WorkerReport: null,
            lastRequeue, Machine: "box-1", machineLive, heartbeat, lastProgress,
            InputTokens: 0, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0,
            CostUsd: null, UsageReportedAt: null,
            Ports: [], Marks: [], Exchange: [], Tail: tail ?? []);
}
