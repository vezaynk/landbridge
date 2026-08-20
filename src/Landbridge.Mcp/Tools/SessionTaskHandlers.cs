using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Landbridge.Mcp.Tools;

/// <summary>
/// MCP Tasks methods projected off the session row. Lead-only.
/// <c>tasks/update</c> is not implemented: answers stay
/// <c>answer_input_request</c> / <c>answer_permission_request</c> /
/// <c>submit_review</c>.
/// </summary>
public sealed class SessionTaskHandlers(SessionStore store, IHttpContextAccessor http)
{
    public const int PageSize = 50;

    public async ValueTask<JsonNode?> GetAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var lead = RequireLead();
        var id = ParseTaskId(Params(request));
        var snap = await store.GetTaskSnapshotAsync(lead.Team, id, ct)
            ?? throw NotFound(id);
        return JsonSerializer.SerializeToNode(ToJson(snap));
    }

    public async ValueTask<JsonNode?> ListAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var lead = RequireLead();
        var p = Params(request, allowMissing: true);
        var cursor = p?["cursor"]?.GetValue<string>();
        try
        {
            var (tasks, next) = await store.ListTaskSnapshotsAsync(lead.Team, cursor, PageSize, ct);
            return JsonSerializer.SerializeToNode(new TaskListJson
            {
                Tasks = tasks.Select(ToJson).ToArray(),
                NextCursor = next,
            });
        }
        catch (ArgumentException)
        {
            throw new McpProtocolException("invalid cursor", McpErrorCode.InvalidParams);
        }
    }

    public async ValueTask<JsonNode?> CancelAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var lead = RequireLead();
        var id = ParseTaskId(Params(request));
        var snap = await store.GetTaskSnapshotAsync(lead.Team, id, ct)
            ?? throw NotFound(id);
        if (SessionTaskProjection.Status(snap.Session) is SessionTaskStatus.Completed
            or SessionTaskStatus.Cancelled)
        {
            throw new McpProtocolException(
                $"Cannot cancel task: already in terminal status '{SessionTaskProjection.WireStatus(SessionTaskProjection.Status(snap.Session))}'",
                McpErrorCode.InvalidParams);
        }

        var applied = await store.ApplyAsync(id, new Cancel(new LeadClaim(lead.Team), CancelDisposition.Preserve), ct);
        return applied switch
        {
            StoreResult.Applied ok => JsonSerializer.SerializeToNode(
                ToJson(new SessionStore.SessionTaskSnapshot(ok.Session, snap.CreatedAt, DateTimeOffset.UtcNow))),
            StoreResult.Rejected r => throw new McpProtocolException(r.Reason, McpErrorCode.InvalidParams),
            StoreResult.NotFound => throw NotFound(id),
            StoreResult.Conflict c => throw new McpProtocolException(c.Reason, McpErrorCode.InternalError),
            _ => throw new McpProtocolException("cancel failed", McpErrorCode.InternalError),
        };
    }

    private Principal.Lead RequireLead()
    {
        var user = http.HttpContext?.User
            ?? throw new McpProtocolException("not authenticated", McpErrorCode.InvalidRequest);
        if (LandbridgeClaims.AsEvictedLead(user) is { } evicted)
            throw new McpProtocolException(
                $"your lead claim on team {evicted.Team.Value:N} was taken over",
                McpErrorCode.InvalidRequest);
        return LandbridgeClaims.AsLeadPrincipal(user)
            ?? throw new McpProtocolException("tasks are a Lead surface", McpErrorCode.InvalidRequest);
    }

    private static JsonObject? Params(JsonRpcRequest request, bool allowMissing = false)
    {
        if (request.Params is null)
        {
            if (allowMissing) return null;
            throw new McpProtocolException("invalid params", McpErrorCode.InvalidParams);
        }

        return request.Params.Deserialize<JsonObject>()
            ?? throw new McpProtocolException("invalid params", McpErrorCode.InvalidParams);
    }

    private static SessionId ParseTaskId(JsonObject? p)
    {
        var raw = p?["taskId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(raw))
            throw new McpProtocolException("taskId is required", McpErrorCode.InvalidParams);
        if (!Guid.TryParse(raw, out var id))
            throw new McpProtocolException("invalid taskId", McpErrorCode.InvalidParams);
        return new SessionId(id);
    }

    private static McpProtocolException NotFound(SessionId id) =>
        new($"Failed to retrieve task: Task not found", McpErrorCode.InvalidParams);

    private static TaskJson ToJson(SessionStore.SessionTaskSnapshot snap)
    {
        var status = SessionTaskProjection.Status(snap.Session);
        return new TaskJson
        {
            TaskId = snap.Session.Id.ToString(),
            Status = SessionTaskProjection.WireStatus(status),
            StatusMessage = SessionTaskProjection.StatusMessage(snap.Session),
            CreatedAt = snap.CreatedAt.ToString("O"),
            LastUpdatedAt = snap.LastUpdatedAt.ToString("O"),
            Ttl = null,
            PollInterval = SessionTaskProjection.DefaultPollIntervalMs,
        };
    }

    private sealed class TaskJson
    {
        [JsonPropertyName("taskId")] public required string TaskId { get; init; }
        [JsonPropertyName("status")] public required string Status { get; init; }
        [JsonPropertyName("statusMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StatusMessage { get; init; }
        [JsonPropertyName("createdAt")] public required string CreatedAt { get; init; }
        [JsonPropertyName("lastUpdatedAt")] public required string LastUpdatedAt { get; init; }
        [JsonPropertyName("ttl")] public int? Ttl { get; init; }
        [JsonPropertyName("pollInterval")] public int PollInterval { get; init; }
    }

    private sealed class TaskListJson
    {
        [JsonPropertyName("tasks")] public required TaskJson[] Tasks { get; init; }
        [JsonPropertyName("nextCursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NextCursor { get; init; }
    }
}
