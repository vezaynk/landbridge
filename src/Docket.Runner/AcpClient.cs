using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Docket.Runner;

/// <summary>
/// ACP client: JSON-RPC 2.0, one NDJSON object per line, over the agent stdio.
/// docketd is the Client; the profile spawn is the Agent.
/// </summary>
internal sealed class AcpClient : IAsyncDisposable
{
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private Task? _reader;

    public event Action<string, JsonElement>? SessionUpdate;
    public event Action<int, JsonElement>? PermissionRequested;

    public AcpClient(Stream stdin, Stream stdout)
    {
        _stdin = new StreamWriter(stdin, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        _stdout = new StreamReader(stdout, Encoding.UTF8);
    }

    public void Start() => _reader = Task.Run(ReadLoopAsync);

    public Task InitializeAsync(CancellationToken ct = default) =>
        RequestAsync("initialize", """
            {"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false},"clientInfo":{"name":"docketd","version":"0"}}
            """, ct);

    public async Task<string> SessionNewAsync(
        string cwd, string? mcpUrl, string workerToken, CancellationToken ct = default)
    {
        var raw = await RequestAsync("session/new", SessionParamsJson(cwd, sessionId: null, mcpUrl, workerToken), ct);
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("sessionId", out var id)
            || id.GetString() is not { Length: > 0 } sessionId)
            throw new InvalidOperationException("ACP session/new returned no sessionId");
        return sessionId;
    }

    public Task SessionLoadAsync(
        string sessionId, string cwd, string? mcpUrl, string workerToken, CancellationToken ct = default) =>
        RequestAsync("session/load", SessionParamsJson(cwd, sessionId, mcpUrl, workerToken), ct);

    public Task SessionPromptAsync(string sessionId, string text, CancellationToken ct = default)
    {
        var json = "{\"sessionId\":" + JsonVal(sessionId) + ",\"prompt\":[{\"type\":\"text\",\"text\":"
                   + JsonVal(text) + "}]}";
        return RequestAsync("session/prompt", json, ct);
    }

    public Task SessionCancelAsync(string sessionId) =>
        WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"session/cancel\",\"params\":{\"sessionId\":"
                       + JsonVal(sessionId) + "}}");

    public Task AnswerPermissionAsync(int requestId, string optionId) =>
        WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":" + requestId
                       + ",\"result\":{\"outcome\":{\"outcome\":\"selected\",\"optionId\":"
                       + JsonVal(optionId) + "}}}");

    public Task CancelPermissionAsync(int requestId) =>
        WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":" + requestId
                       + ",\"result\":{\"outcome\":{\"outcome\":\"cancelled\"}}}");

    private static string SessionParamsJson(string cwd, string? sessionId, string? mcpUrl, string workerToken)
    {
        var sb = new StringBuilder();
        sb.Append("{\"cwd\":").Append(JsonVal(cwd));
        if (sessionId is not null)
            sb.Append(",\"sessionId\":").Append(JsonVal(sessionId));
        sb.Append(",\"mcpServers\":[");
        if (!string.IsNullOrWhiteSpace(mcpUrl) && workerToken.Length > 0)
        {
            sb.Append("{\"type\":\"http\",\"name\":\"docket\",\"url\":").Append(JsonVal(mcpUrl))
              .Append(",\"headers\":[{\"name\":\"Authorization\",\"value\":")
              .Append(JsonVal("Bearer " + workerToken)).Append("}]}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string JsonVal(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private async Task<string> RequestAsync(string method, string paramsJson, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":" + JsonVal(method)
                             + ",\"params\":" + paramsJson + "}");
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.WaitAsync(ct);
    }

    private Task WriteLineAsync(string line) => _stdin.WriteLineAsync(line);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _stdout.ReadLineAsync(_cts.Token);
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;

                JsonElement msg;
                try { msg = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { continue; }

                if (msg.TryGetProperty("method", out var methodEl) && methodEl.GetString() is { } method)
                {
                    if (method == "session/update" && msg.TryGetProperty("params", out var p))
                    {
                        var sessionId = p.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? "" : "";
                        var update = p.TryGetProperty("update", out var u) ? u : p;
                        SessionUpdate?.Invoke(sessionId, update.Clone());
                    }
                    else if (method == "session/request_permission"
                             && msg.TryGetProperty("id", out var reqId)
                             && reqId.TryGetInt32(out var id)
                             && msg.TryGetProperty("params", out var perm))
                    {
                        PermissionRequested?.Invoke(id, perm.Clone());
                    }
                    continue;
                }

                if (msg.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var pendingId)
                    && _pending.TryRemove(pendingId, out var tcs))
                {
                    if (msg.TryGetProperty("error", out var err))
                        tcs.TrySetException(new InvalidOperationException("ACP error: " + err.GetRawText()));
                    else
                        tcs.TrySetResult(msg.TryGetProperty("result", out var result)
                            ? result.GetRawText()
                            : "null");
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* shutdown */
        }
        finally
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* best effort */ }
        }
        _cts.Dispose();
    }
}
