using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Docket.Contracts;
using Docket.Core;

namespace Docket.Runner;

/// <summary>How a <c>stop</c> was delivered (§10). Reported back so the
/// conformance run can confirm message-delivery actually reached the agent.</summary>
public enum StopDelivery
{
    /// <summary>Injected as a turn the agent reads and honours (§10).</summary>
    Message,

    /// <summary>A signal — cannot carry a disposition; TTL kill still backs it (§10).</summary>
    Signal,

    /// <summary><c>ttl == 0</c>: killed immediately without waiting for ack (§9 check 12).</summary>
    ImmediateKill,

    /// <summary>No such task here; the command is moot (§10 buffering — commands are best-effort).</summary>
    NotRunning,
}

/// <summary>The runner's acknowledgement of a <c>stop</c>.</summary>
public readonly record struct StopAck(bool Delivered, StopDelivery Delivery);

/// <summary>Process supervision, spec §10 runner capabilities.</summary>
public interface IProcessSupervisor
{
    /// <summary>Spawns the harness for a dispatched task and emits <c>started</c>.</summary>
    TaskId Spawn(DispatchCommand dispatch, ProfileConfig profile, string machineId);

    /// <summary>
    /// Graceful stop: deliver the disposition, arm a TTL timer, hard-kill on
    /// expiry (§10, §11). <c>ttl == 0</c> kills immediately.
    /// </summary>
    Task<StopAck> StopAsync(TaskId task, TimeSpan ttl, StopDisposition disposition, string? reason, CancellationToken ct);

    /// <summary>Immediate group/tree kill of one task (§10).</summary>
    bool Kill(TaskId task);

    /// <summary>Clean shutdown: kill everything the runner started (§10 runner restart).</summary>
    void KillAll();

    int RunningFor(string profile);
    int RunningTotal { get; }
    IReadOnlyCollection<TaskId> RunningTasks { get; }
}

/// <summary>One supervised harness process and the per-task state around it.</summary>
public sealed class SupervisedTask
{
    public required TaskId Task { get; init; }
    public required string Profile { get; init; }
    public required Process Process { get; init; }
    public required string WorkDir { get; init; }
    public required StopConfig Stop { get; init; }

    /// <summary>Last per-task liveness signal (started/tool-call), §10.</summary>
    public DateTimeOffset LastActivityAt { get; set; }

    public bool StopRequested { get; set; }
    internal ITimer? TtlTimer { get; set; }

    public bool ProcessAlive
    {
        get
        {
            try { return !Process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }
}

/// <summary>
/// Spawns and supervises harness processes, spec §10. <b>No shell, ever</b>:
/// <c>command</c> is argv passed to <see cref="ProcessStartInfo.ArgumentList"/>
/// (§10). Every spawn is stamped with <c>DOCKET_MACHINE_ID</c> and
/// <c>DOCKET_TASK_ID</c> (§10, not configurable), started in
/// <c>{work_root}/{task_id}</c> (§10 work_root note), and killed as a whole
/// tree so children — subagents, dev servers — go down with the parent (§10
/// process groups). The portable tree-kill (<see cref="Process.Kill(bool)"/>)
/// is the group-kill baseline the conventions call for; each task is its own
/// tree, so a kill leaves siblings untouched.
/// </summary>
public sealed class ProcessSupervisor : IProcessSupervisor
{
    private readonly MachineConfig _machine;
    private readonly OutboundEventRing _ring;
    private readonly TimeProvider _clock;
    private readonly StrayReaper? _taskReaper;
    private readonly ConcurrentDictionary<TaskId, SupervisedTask> _tasks = new();

    public ProcessSupervisor(MachineConfig machine, OutboundEventRing ring, TimeProvider clock, StrayReaper? taskReaper = null)
    {
        _machine = machine;
        _ring = ring;
        _clock = clock;
        _taskReaper = taskReaper;
    }

    public int RunningTotal => _tasks.Count;

    public IReadOnlyCollection<TaskId> RunningTasks => _tasks.Keys.ToArray();

    public int RunningFor(string profile) =>
        _tasks.Values.Count(t => string.Equals(t.Profile, profile, StringComparison.Ordinal));

    public bool TryGet(TaskId task, out SupervisedTask supervised) => _tasks.TryGetValue(task, out supervised!);

    public TaskId Spawn(DispatchCommand dispatch, ProfileConfig profile, string machineId)
    {
        if (profile.Spawn.Count == 0)
            throw new InvalidOperationException($"profile '{profile.Name}' has an empty spawn argv");

        var workDir = Path.Combine(_machine.WorkRoot, dispatch.Task.ToString());
        Directory.CreateDirectory(workDir);

        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["task_id"] = dispatch.Task.ToString(),
            ["machine_id"] = machineId,
            ["work_dir"] = workDir,
            ["budget"] = dispatch.BudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "",
        };
        if (dispatch.SpawnSubstitutions is not null)
            foreach (var (key, value) in dispatch.SpawnSubstitutions)
                substitutions[key] = value;

        // §13: the worker token's generated MCP config lives in the task dir, 0600.
        if (dispatch.McpConfigJson is not null)
        {
            var mcpPath = Path.Combine(workDir, "mcp.json");
            File.WriteAllText(mcpPath, dispatch.McpConfigJson);
            SetOwnerOnly(mcpPath);
            substitutions["mcp_config"] = mcpPath;
        }

        var argv = profile.Spawn.Select(a => Substitute(a, substitutions)).ToArray();

        var psi = new ProcessStartInfo(argv[0])
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            // stdin is redirected for EVERY spawn and its write end is held open for
            // the child's lifetime (we never close StandardInput here). That held
            // pipe IS the dead-man's signal: if docketd dies — even under SIGKILL —
            // the OS closes the write end and a well-behaved harness sees EOF on
            // stdin and kills its own process tree. This is the cooperative first
            // line of defence the StrayReaper only backstops on restart (§10).
            // Message-mode stop reuses the SAME pipe to inject a stop turn (see
            // StopAsync), so the two uses are compatible. Not redirecting
            // stdout/stderr keeps us off the drain-or-deadlock hook — the transcript
            // is tailed from logs.path, not scraped from stdout.
            RedirectStandardInput = true,
        };
        for (var i = 1; i < argv.Length; i++)
            psi.ArgumentList.Add(argv[i]);

        psi.Environment["DOCKET_MACHINE_ID"] = machineId;
        psi.Environment["DOCKET_TASK_ID"] = dispatch.Task.ToString();
        if (dispatch.WorkerToken.Length > 0)
            psi.Environment["DOCKET_WORKER_TOKEN"] = dispatch.WorkerToken;

        // §1 tracing: hand the worker the current handle span's W3C id so its root
        // span — and its MCP calls back to the plane — continue the one trace.
        // Null when nothing is tracing. The child inherits the rest of docketd's
        // environment as a copy (ProcessStartInfo does not clear it under
        // UseShellExecute=false), so OTEL_EXPORTER_OTLP_ENDPOINT/…_PROTOCOL flow
        // through unchanged and the worker exports to the same collector.
        if (Activity.Current?.Id is { } traceparent)
            psi.Environment["DOCKET_TRACEPARENT"] = traceparent;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var supervised = new SupervisedTask
        {
            Task = dispatch.Task,
            Profile = profile.Name,
            Process = process,
            WorkDir = workDir,
            Stop = profile.Stop,
            LastActivityAt = _clock.GetUtcNow(),
        };
        process.Exited += (_, _) => OnExited(supervised, machineId);
        _tasks[dispatch.Task] = supervised;

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false");
        }
        catch
        {
            _tasks.TryRemove(dispatch.Task, out _);
            process.Dispose();
            throw;
        }

        // §10: started (harness up) is distinct from dispatch ack. Process-spawned
        // is docketd's own observation; a richer events.source refines it upstream.
        _ring.Enqueue(new StartedEvent(dispatch.Task, _clock.GetUtcNow()));
        return dispatch.Task;
    }

    /// <summary>Records a per-task liveness signal from the events source (§10).</summary>
    public void RecordActivity(TaskId task)
    {
        if (_tasks.TryGetValue(task, out var supervised))
            supervised.LastActivityAt = _clock.GetUtcNow();
    }

    /// <summary>
    /// Per-task liveness (§10): the task is live only while its process is alive
    /// <em>and</em> a signal has arrived within <paramref name="timeout"/>. The
    /// control plane suspends this while a task is blocked_on_input/parked (§11);
    /// the runner reports the raw derivation.
    /// </summary>
    public bool IsTaskLive(TaskId task, TimeSpan timeout)
    {
        if (!_tasks.TryGetValue(task, out var supervised) || !supervised.ProcessAlive)
            return false;
        return _clock.GetUtcNow() - supervised.LastActivityAt <= timeout;
    }

    public async Task<StopAck> StopAsync(
        TaskId task, TimeSpan ttl, StopDisposition disposition, string? reason, CancellationToken ct)
    {
        if (!_tasks.TryGetValue(task, out var supervised))
            return new StopAck(false, StopDelivery.NotRunning);

        supervised.StopRequested = true;

        // §9 check 12 / §11: TTL=0 kills immediately without waiting for ack.
        if (ttl <= TimeSpan.Zero)
        {
            KillTree(supervised);
            return new StopAck(true, StopDelivery.ImmediateKill);
        }

        var delivery = StopDelivery.Signal;
        if (supervised.Stop.Mode == StopMode.Message && supervised.Process.StartInfo.RedirectStandardInput)
        {
            var message = BuildStopMessage(supervised.Stop, disposition, ttl, reason);
            try
            {
                await supervised.Process.StandardInput.WriteLineAsync(message.AsMemory(), ct);
                await supervised.Process.StandardInput.FlushAsync(ct);
                delivery = StopDelivery.Message;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // Pipe already closed — fall through to the TTL hard-kill backstop.
            }
        }

        // §10, §11: signals are reserved for TTL expiry and kill. Arm the timer;
        // FakeTimeProvider drives it deterministically in tests.
        supervised.TtlTimer = _clock.CreateTimer(_ => KillTree(supervised), null, ttl, Timeout.InfiniteTimeSpan);
        return new StopAck(true, delivery);
    }

    public bool Kill(TaskId task)
    {
        if (!_tasks.TryGetValue(task, out var supervised))
            return false;
        KillTree(supervised);
        return true;
    }

    public void KillAll()
    {
        foreach (var supervised in _tasks.Values)
            KillTree(supervised);
    }

    private void OnExited(SupervisedTask supervised, string machineId)
    {
        _tasks.TryRemove(supervised.Task, out _);
        supervised.TtlTimer?.Dispose();

        int exitCode;
        try { exitCode = supervised.Process.ExitCode; }
        catch (InvalidOperationException) { exitCode = -1; }

        _ring.Enqueue(new ExitedEvent(supervised.Task, exitCode, _clock.GetUtcNow()));

        // §10: task-exit stray cleanup keyed by DOCKET_TASK_ID, best-effort.
        try { _taskReaper?.ReapTask(machineId, supervised.Task.ToString()); }
        catch { /* best effort */ }
    }

    private static void KillTree(SupervisedTask supervised)
    {
        supervised.TtlTimer?.Dispose();
        try
        {
            if (!supervised.Process.HasExited)
                supervised.Process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited, or the OS refused — either way the task is gone.
        }
    }

    private static string Substitute(string arg, IReadOnlyDictionary<string, string> substitutions)
    {
        if (!arg.Contains('{', StringComparison.Ordinal))
            return arg;
        foreach (var (key, value) in substitutions)
            arg = arg.Replace("{" + key + "}", value, StringComparison.Ordinal);
        return arg;
    }

    private static string BuildStopMessage(StopConfig stop, StopDisposition disposition, TimeSpan ttl, string? reason)
    {
        var dispositionName = disposition switch
        {
            StopDisposition.Preserve => "preserve",
            StopDisposition.Discard => "discard",
            StopDisposition.PreserveAndPark => "preserve_and_park",
            _ => "preserve",
        };
        if (!string.IsNullOrEmpty(stop.MessageTemplate))
        {
            return stop.MessageTemplate
                .Replace("{disposition}", dispositionName, StringComparison.Ordinal)
                .Replace("{ttl_seconds}", ((int)ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{reason}", reason ?? "", StringComparison.Ordinal);
        }

        // Default: a compact JSON stop turn. The frozen vocabulary names the
        // command; the harness config names the exact transport shape (§10).
        return System.Text.Json.JsonSerializer.Serialize(
            new StopMessage("stop", dispositionName, (int)ttl.TotalSeconds, reason),
            RunnerStopMessageContext.Default.StopMessage);
    }

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>The default injected stop-turn payload (§10, §11).</summary>
internal sealed record StopMessage(string Type, string Disposition, int TtlSeconds, string? Reason);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower)]
[System.Text.Json.Serialization.JsonSerializable(typeof(StopMessage))]
internal sealed partial class RunnerStopMessageContext : System.Text.Json.Serialization.JsonSerializerContext;
