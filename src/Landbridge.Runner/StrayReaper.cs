using System.Diagnostics;

namespace Landbridge.Runner;

/// <summary>A running process carrying landbridge tags, as discovered by an inventory.</summary>
public readonly record struct TaggedProcess(int Pid, string MachineId, string? SessionId);

/// <summary>
/// Enumerates processes that carry a <c>LANDBRIDGE_MACHINE_ID</c> env var. This is
/// the discovery half of stray cleanup (§10 runner restart) and is inherently
/// platform-specific: reading another process's environment is
/// <c>/proc/&lt;pid&gt;/environ</c> on Linux, and needs privileged syscalls on
/// macOS/Windows — see <see cref="ProcessInventory.ForCurrentPlatform"/>.
/// </summary>
public interface IProcessInventory
{
    IReadOnlyList<TaggedProcess> ListLandbridgeProcesses();
}

/// <summary>Factory + a no-op inventory for platforms without a discovery path yet.</summary>
public static class ProcessInventory
{
    /// <summary>
    /// The best available inventory for the current OS. Linux reads
    /// <c>/proc</c>; macOS reads each process's environment via libproc +
    /// <c>sysctl(KERN_PROCARGS2)</c> (unprivileged, see
    /// <see cref="MacOsProcessInventory"/>). On <b>Windows</b> this is the empty
    /// <see cref="NullProcessInventory"/> <em>by design</em>, not a deferral: each
    /// worker is sealed at spawn into a kill-on-close Job Object
    /// (<see cref="WindowsJobObject"/>, §10 Windows containment), so landbridged's death — by any cause —
    /// makes the OS terminate the whole worker tree with no discovery to run. Any
    /// other platform also returns <see cref="NullProcessInventory"/>, there as a
    /// documented deferral. The <b>kill</b> half of stray cleanup is portable
    /// regardless (see <see cref="StrayReaper"/>).
    /// </summary>
    public static IProcessInventory ForCurrentPlatform() =>
        OperatingSystem.IsLinux() ? new ProcFsProcessInventory()
        : OperatingSystem.IsMacOS() ? new MacOsProcessInventory()
        : new NullProcessInventory();
}

/// <summary>
/// Discovers nothing. On Windows this is <em>by design</em> — the per-worker
/// kill-on-close Job Object (<see cref="WindowsJobObject"/>, §10 Windows containment) is the containment
/// guarantee, so there is nothing to discover on restart. On any other unsupported
/// platform it is a documented deferral of env-scan discovery, never a silent
/// failure.
/// </summary>
public sealed class NullProcessInventory : IProcessInventory
{
    public IReadOnlyList<TaggedProcess> ListLandbridgeProcesses() => [];
}

/// <summary>
/// Linux inventory: scans <c>/proc/&lt;pid&gt;/environ</c> for the landbridge tags.
/// Guarded to Linux; on other platforms the factory hands back
/// <see cref="NullProcessInventory"/> instead.
/// </summary>
public sealed class ProcFsProcessInventory : IProcessInventory
{
    public IReadOnlyList<TaggedProcess> ListLandbridgeProcesses()
    {
        if (!OperatingSystem.IsLinux())
            return [];

        var found = new List<TaggedProcess>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid))
                continue;

            string machineId;
            string? sessionId;
            try
            {
                var raw = File.ReadAllText(Path.Combine(dir, "environ"));
                var env = ParseEnviron(raw);
                if (!env.TryGetValue("LANDBRIDGE_MACHINE_ID", out machineId!))
                    continue;
                env.TryGetValue("LANDBRIDGE_SESSION_ID", out sessionId);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // process gone or not ours to read
            }

            found.Add(new TaggedProcess(pid, machineId, sessionId));
        }

        return found;
    }

    private static Dictionary<string, string> ParseEnviron(string raw)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                env[entry[..eq]] = entry[(eq + 1)..];
        }
        return env;
    }
}

/// <summary>Kills stray harness processes on restart (§10 runner restart).</summary>
public interface IStrayReaper
{
    /// <summary>Kills every stray tagged with <paramref name="machineId"/>; returns the count.</summary>
    int Reap(string machineId);
}

/// <summary>
/// Spec §10: <b>on start, landbridged kills any stray harness processes before
/// accepting dispatch</b> — the guarantee that survives a SIGKILLed daemon,
/// since clean shutdown cannot be relied on. Discovery is delegated to an
/// <see cref="IProcessInventory"/> (platform-specific); the kill itself is
/// portable via <see cref="Process.Kill(bool)"/> with the whole tree, matching
/// the group-kill semantics of live tasks (§10).
///
/// <para>On Windows there are no strays to reap: each worker lives in a
/// kill-on-close Job Object (<see cref="WindowsJobObject"/>, §10 Windows containment) that the OS tears
/// down the instant landbridged dies, so the inventory there is
/// <see cref="NullProcessInventory"/> by design and this restart sweep simply finds
/// nothing.</para>
/// </summary>
public sealed class StrayReaper(IProcessInventory inventory, int selfPid) : IStrayReaper
{
    public int Reap(string machineId)
    {
        var reaped = 0;
        foreach (var proc in inventory.ListLandbridgeProcesses())
        {
            if (proc.Pid == selfPid || !string.Equals(proc.MachineId, machineId, StringComparison.Ordinal))
                continue;
            if (KillTree(proc.Pid))
                reaped++;
        }
        return reaped;
    }

    /// <summary>
    /// Per-task exit cleanup (§10): a dev server that <c>setsid</c>ed out of the
    /// task's group survives group kill and keeps the port. This catches it
    /// while the task id is still known.
    /// </summary>
    public int ReapSession(string machineId, string sessionId)
    {
        var reaped = 0;
        foreach (var proc in inventory.ListLandbridgeProcesses())
        {
            if (proc.Pid == selfPid
                || !string.Equals(proc.MachineId, machineId, StringComparison.Ordinal)
                || !string.Equals(proc.SessionId, sessionId, StringComparison.Ordinal))
            {
                continue;
            }
            if (KillTree(proc.Pid))
                reaped++;
        }
        return reaped;
    }

    private static bool KillTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // already gone, or not killable
        }
    }
}
