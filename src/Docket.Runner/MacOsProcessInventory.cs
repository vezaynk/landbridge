using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Docket.Runner;

/// <summary>
/// macOS inventory: the <c>/proc</c> equivalent for Darwin, which has no
/// <c>/proc</c>. Enumerates every pid with libproc's <c>proc_listallpids</c>,
/// then reads each process's argv+environment via <c>sysctl(KERN_PROCARGS2)</c>
/// and scans it for the docket tags. All unprivileged: a same-user process's
/// args/env are readable without root, and <c>KERN_PROCARGS2</c> simply fails
/// (skipped) for processes we may not read — which is exactly the set that
/// isn't ours to reap. No cgroups, no launchd, no privileged syscall. Guarded
/// to macOS; the factory hands other platforms a different inventory.
/// </summary>
public sealed partial class MacOsProcessInventory : IProcessInventory
{
    // sysctl mib constants (<sys/sysctl.h>).
    private const int CtlKern = 1;       // CTL_KERN
    private const int KernArgmax = 8;    // KERN_ARGMAX
    private const int KernProcargs2 = 49; // KERN_PROCARGS2

    // KERN_ARGMAX is a system-wide constant (~1 MB); this is only a fallback if
    // the sysctl query for it ever fails.
    private const int ArgMaxFallback = 1 << 20;

    // Our tags are env-only (ProcessSupervision.Spawn injects them into the
    // child's environment, never argv), so scanning the whole KERN_PROCARGS2
    // string region past the argc header for these prefixes is unambiguous.
    private static ReadOnlySpan<byte> MachineIdPrefix => "DOCKET_MACHINE_ID="u8;
    private static ReadOnlySpan<byte> TaskIdPrefix => "DOCKET_TASK_ID="u8;

    public IReadOnlyList<TaggedProcess> ListDocketProcesses()
    {
        if (!OperatingSystem.IsMacOS())
            return [];

        return Scan();
    }

    [SupportedOSPlatform("macos")]
    private static List<TaggedProcess> Scan()
    {
        var found = new List<TaggedProcess>();
        var selfPid = Environment.ProcessId;

        var pids = ListAllPids();
        if (pids.Length == 0)
            return found;

        var argMax = QueryArgMax();
        var buffer = ArrayPool<byte>.Shared.Rent(argMax);
        try
        {
            foreach (var pid in pids)
            {
                if (pid <= 0 || pid == selfPid)
                    continue;

                if (TryReadTags(pid, buffer, out var machineId, out var taskId) && machineId is not null)
                    found.Add(new TaggedProcess(pid, machineId, taskId));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return found;
    }

    /// <summary>
    /// Snapshots all pids on the machine via libproc. <c>proc_listallpids</c>
    /// returns the pid count (it divides the kernel's byte count by
    /// <c>sizeof(int)</c>); a null buffer asks only for that count, which we then
    /// over-allocate to absorb processes that spawn between the two calls.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static unsafe int[] ListAllPids()
    {
        var count = proc_listallpids(null, 0);
        if (count <= 0)
            count = 4096; // kernel declined to size it; try a generous buffer anyway

        var capacity = (count * 2) + 1024;
        var rented = ArrayPool<int>.Shared.Rent(capacity);
        try
        {
            int returned;
            fixed (int* p = rented)
                returned = proc_listallpids(p, rented.Length * sizeof(int));
            if (returned <= 0)
                return [];

            returned = Math.Min(returned, rented.Length);
            return rented.AsSpan(0, returned).ToArray();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    [SupportedOSPlatform("macos")]
    private static int QueryArgMax()
    {
        int argMax = 0;
        nuint size = sizeof(int);
        int rc;
        unsafe
        {
            Span<int> mib = [CtlKern, KernArgmax];
            fixed (int* mibPtr = mib)
                rc = sysctl(mibPtr, (uint)mib.Length, &argMax, &size, null, 0);
        }
        return rc == 0 && argMax > 0 ? argMax : ArgMaxFallback;
    }

    /// <summary>
    /// Reads one process's argv+env block via <c>sysctl(KERN_PROCARGS2)</c> into
    /// <paramref name="buffer"/> and scans it for the docket tags. A failed
    /// sysctl (process exited, or not ours to read) returns <c>false</c> — the
    /// natural, uid-free filter to same-user processes.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static bool TryReadTags(int pid, byte[] buffer, out string? machineId, out string? taskId)
    {
        machineId = null;
        taskId = null;

        nuint size = (nuint)buffer.Length;
        int rc;
        unsafe
        {
            Span<int> mib = [CtlKern, KernProcargs2, pid];
            fixed (int* mibPtr = mib)
            fixed (byte* bufPtr = buffer)
                rc = sysctl(mibPtr, (uint)mib.Length, bufPtr, &size, null, 0);
        }
        if (rc != 0 || size <= sizeof(int))
            return false;

        // Layout: [int argc][exec path\0][\0 padding][argc argv\0 strings][env
        // KEY=VALUE\0 strings]. Skip the argc header and scan the NUL-delimited
        // strings for our env-only prefixes; argv can't collide with them.
        var region = buffer.AsSpan(sizeof(int), (int)size - sizeof(int));
        ScanForTags(region, ref machineId, ref taskId);
        return true;
    }

    private static void ScanForTags(ReadOnlySpan<byte> region, ref string? machineId, ref string? taskId)
    {
        var start = 0;
        for (var i = 0; i <= region.Length; i++)
        {
            if (i != region.Length && region[i] != 0)
                continue;

            if (i > start)
            {
                var segment = region[start..i];
                if (machineId is null && segment.StartsWith(MachineIdPrefix))
                    machineId = Encoding.UTF8.GetString(segment[MachineIdPrefix.Length..]);
                else if (taskId is null && segment.StartsWith(TaskIdPrefix))
                    taskId = Encoding.UTF8.GetString(segment[TaskIdPrefix.Length..]);
            }

            if (machineId is not null && taskId is not null)
                return; // both found; the rest of the block can't tell us more

            start = i + 1;
        }
    }

    [LibraryImport("libproc")]
    private static unsafe partial int proc_listallpids(int* buffer, int buffersize);

    [LibraryImport("libc")]
    private static unsafe partial int sysctl(
        int* name, uint namelen, void* oldp, nuint* oldlenp, void* newp, nuint newlen);
}
