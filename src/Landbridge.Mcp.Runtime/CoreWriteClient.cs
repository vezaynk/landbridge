using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.ControlPlane;
using Landbridge.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Landbridge.Mcp;

/// <summary>
/// Façade → Core writes. Same Bearer as the inbound call. When
/// <c>Landbridge:CoreUrl</c> is unset (tests), callers keep using
/// <see cref="SessionStore"/> in-process.
/// </summary>
public sealed class CoreWriteClient(HttpClient http, ILogger<CoreWriteClient> logger)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public bool Enabled => http.BaseAddress is not null;

    public async Task<CoreStoreReply> PostAsync<T>(string path, string bearer, T body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await http.SendAsync(req, ct);
        var reply = await resp.Content.ReadFromJsonAsync<CoreStoreReply>(Json, ct)
            ?? new CoreStoreReply("conflict", Reason: $"core {path} returned {(int)resp.StatusCode} with no body");
        if (!resp.IsSuccessStatusCode && reply.Status == "applied")
            reply = reply with { Status = "conflict", Reason = $"core {path} HTTP {(int)resp.StatusCode}" };
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("core POST {Path} {Status}: {Reason}", path, (int)resp.StatusCode, reply.Reason);
        return reply;
    }

    public async Task<T?> PostAsAsync<T>(string path, string bearer, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await http.SendAsync(req, ct);
        T? parsed = default;
        try
        {
            parsed = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (JsonException)
        {
        }
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("core POST {Path} {Status}", path, (int)resp.StatusCode);
        return parsed;
    }
}

public sealed record CoreStoreReply(
    string Status,
    string? State = null,
    string? SessionId = null,
    string? Slug = null,
    string? Rule = null,
    string? Reason = null,
    string? Detail = null)
{
    public static CoreStoreReply From(StoreResult result) => result switch
    {
        StoreResult.Applied a => new(
            "applied",
            a.Session.State.ToString(),
            a.Session.Id.Value.ToString("D"),
            Reason: $"ok: session is now {a.Session.State}"),
        StoreResult.Rejected r => new("rejected", Rule: r.Rule.ToString(), Reason: r.Reason),
        StoreResult.NotFound n => new("not_found", Reason: n.Reason),
        StoreResult.Conflict c => new("conflict", Reason: c.Reason),
        _ => new("conflict", Reason: "unknown store result"),
    };

    public static CoreStoreReply FromCommand(CommandRow row) => row.Status switch
    {
        CommandRow.Applied => new(
            "applied",
            State: row.Reason,
            SessionId: row.SessionId.ToString("D"),
            Slug: row.Slug,
            Reason: row.Slug ?? row.Reason),
        CommandRow.Rejected => new("rejected", Rule: row.Rule, Reason: row.Reason),
        _ => new("accepted", SessionId: row.SessionId.ToString("D"), Reason: row.Id.ToString("D")),
    };

    public string Describe() => Status == "applied"
        ? Reason ?? "ok"
        : throw new McpException(Status switch
        {
            "rejected" => $"rejected ({Rule}): {Reason}",
            "not_found" => Reason ?? "not found",
            _ => $"conflict: {Reason}",
        });
}

public sealed record CoreCreateSessionBody(string TeamId, string Description, string Profile, string? SessionId = null);
public sealed record CoreSessionBody(string TeamId, int? TtlSeconds = null, string? Answer = null, string? Text = null, string? Option = null, string? Message = null, string? ResultReference = null, string? Report = null, string? Kind = null, string? Name = null, int? Port = null);
public sealed record CoreBindBody(string MachineId);
public sealed record CoreProcessStartBody(string Name, string[] Spawn, string? WorkingDirectory, Dictionary<string, string>? Env, bool OpenStdin);
public sealed record CoreProcessBody(string Name, string? Data = null, bool AppendNewline = true);
public sealed record CoreProcessStartReply(bool Started, string? LogPath, string? Refusal);
public sealed record CoreProcessActionReply(bool Ok, string? Refusal, int? Value);
public sealed record CoreForwardBody(string ServiceName, string? TeamId = null);
public sealed record CoreForwardReply(bool Ok, string? Host, int? Port, string? ForwardId, DateTimeOffset? ExpiresAt, string? Reason, string? Rule = null);
public sealed record CorePreviewMintBody(string ServiceName, bool IsPublic = false, int? TtlMinutes = null, string? TeamId = null, string? SessionId = null);
public sealed record CorePreviewReply(bool Ok, string? Url = null, string? Auth = null, DateTimeOffset? ExpiresAt = null, Guid? PreviewId = null, string? Reason = null, string? Label = null);
public sealed record CorePreviewPatchBody(Guid PreviewId, bool? IsPublic = null, bool Revoke = false);
public sealed record CoreFrictionBody(string Message, string? TeamId = null);
public sealed record CoreRevokeMachineBody(string MachineId);
public sealed record CoreRevokeMachineReply(bool Ok, bool ChannelClosed, int SessionsRequeued, int WorkersRevoked, string? Reason);
public sealed record CoreCloseForwardBody(Guid ForwardId);
public sealed record CoreCloseForwardReply(bool Ok, string? ServiceName = null, string? Reason = null);
