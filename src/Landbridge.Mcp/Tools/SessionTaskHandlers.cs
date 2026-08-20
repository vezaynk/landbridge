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
/// MCP Tasks methods projected off the message envelope. Lead-only.
/// Closing an envelope is answering, reviewing, or <c>cancel_session</c> —
/// not <c>tasks/cancel</c>.
/// </summary>
public sealed class SessionTaskHandlers(SessionStore store, IHttpContextAccessor http)
{
    public const int PageSize = 50;

    public async ValueTask<JsonNode?> GetAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var lead = RequireLead();
        var id = ParseTaskId(Params(request));
        var snap = await store.GetTaskSnapshotAsync(lead.Team, id, ct)
            ?? throw NotFound();
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

    public ValueTask<JsonNode?> CancelAsync(JsonRpcRequest request, CancellationToken ct)
    {
        _ = request;
        _ = ct;
        RequireLead();
        throw new McpProtocolException(
            "tasks/cancel does not close a session; answer, submit_review, or cancel_session",
            McpErrorCode.InvalidParams);
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

    private static Guid ParseTaskId(JsonObject? p)
    {
        var raw = p?["taskId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(raw))
            throw new McpProtocolException("taskId is required", McpErrorCode.InvalidParams);
        if (!Guid.TryParse(raw, out var id))
            throw new McpProtocolException("invalid taskId", McpErrorCode.InvalidParams);
        return id;
    }

    private static McpProtocolException NotFound() =>
        new("Failed to retrieve task: Task not found", McpErrorCode.InvalidParams);

    private static TaskJson ToJson(SessionStore.SessionTaskSnapshot snap) => new()
    {
        TaskId = snap.TaskId.ToString(),
        Status = SessionTaskProjection.WireStatus(snap.Status),
        StatusMessage = snap.StatusMessage,
        CreatedAt = snap.CreatedAt.ToString("O"),
        LastUpdatedAt = snap.LastUpdatedAt.ToString("O"),
        Ttl = null,
        PollInterval = SessionTaskProjection.DefaultPollIntervalMs,
    };

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
