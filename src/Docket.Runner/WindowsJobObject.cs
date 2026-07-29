using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Docket.Runner;

/// <summary>
/// A Windows Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> — the Windows
/// half of stray cleanup (§10 / §10 Windows containment). docketd seals every worker it spawns into its
/// own job (one job per task) and owns the only handle. When docketd dies by
/// <em>any</em> cause — clean exit, unhandled crash, or the process being killed —
/// the OS closes that handle, and kill-on-close makes the kernel terminate every
/// process still in the job: the worker and all its descendants, grandchildren that
/// escaped the group and detached dev servers included. That is the guarantee the
/// Linux/macOS <see cref="StrayReaper"/> reconstructs by env-scan discovery, so on
/// Windows no discovery is needed — the OS does it, unprivileged, for a process's own
/// children.
///
/// <para>Kills are still cross-platform: <see cref="ProcessSupervisor"/> tree-kills via
/// <see cref="System.Diagnostics.Process.Kill(bool)"/> everywhere and uses
/// <see cref="TerminateAndClose"/> on top on Windows so an escaped process is swept up
/// and the exit code is deterministic. The job is the Windows <em>extra</em>, never
/// the sole mechanism.</para>
///
/// <para>AOT-clean: <see cref="LibraryImportAttribute"/> on kernel32 with blittable
/// signatures. Every method is Windows-only; callers guard with
/// <see cref="OperatingSystem.IsWindows"/> (the class itself is left unattributed so a
/// cross-platform type may hold a nullable field of it without a platform guard).</para>
/// </summary>
internal sealed partial class WindowsJobObject
{
    // JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation.
    private const int JobObjectExtendedLimitInformation = 9;

    // JOBOBJECT_BASIC_LIMIT_INFORMATION.LimitFlags: kill every process in the job
    // when its last handle closes. This is the whole point — it makes docketd's
    // death the worker tree's death with nothing to run and nothing to discover.
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    // Exit code handed to processes swept by an explicit TerminateJobObject. Matches
    // the spirit of Process.Kill (non-zero == killed, not a clean exit).
    private const uint KilledExitCode = 1;

    private readonly object _gate = new();
    private nint _handle;

    private WindowsJobObject(nint handle) => _handle = handle;

    /// <summary>
    /// Creates a kill-on-close job and assigns <paramref name="processHandle"/> to it.
    /// Returns null (with a reason) instead of throwing when creation or assignment
    /// fails, so the caller can degrade rather than fail the spawn: CI runners
    /// (GitHub Actions among them) wrap processes in their own Job Objects, and though
    /// Windows 8+ nests jobs, an incompatible outer job can still refuse the
    /// assignment. The child processes created <em>after</em> assignment are placed in
    /// the job automatically by the kernel, which is how the worker's grandchildren
    /// are contained.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static WindowsJobObject? TryCreateAndAssign(nint processHandle, out string? failureReason)
    {
        failureReason = null;

        var handle = CreateJobObjectW(nint.Zero, nint.Zero);
        if (handle == nint.Zero)
        {
            failureReason = $"CreateJobObjectW failed (win32 {Marshal.GetLastWin32Error()})";
            return null;
        }

        var job = new WindowsJobObject(handle);

        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(
                handle, JobObjectExtendedLimitInformation, in info,
                (uint)Unsafe.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            failureReason = $"SetInformationJobObject failed (win32 {Marshal.GetLastWin32Error()})";
            job.Close();
            return null;
        }

        if (!AssignProcessToJobObject(handle, processHandle))
        {
            failureReason = $"AssignProcessToJobObject failed (win32 {Marshal.GetLastWin32Error()})";
            job.Close();
            return null;
        }

        return job;
    }

    /// <summary>True when <paramref name="processHandle"/> is a member of THIS job
    /// (not merely of some job — nested outer jobs on CI don't confuse it).</summary>
    [SupportedOSPlatform("windows")]
    public bool ContainsProcess(nint processHandle)
    {
        lock (_gate)
        {
            if (_handle == nint.Zero)
                return false;
            return IsProcessInJob(processHandle, _handle, out var inJob) && inJob;
        }
    }

    /// <summary>
    /// Explicit kill (§10 Windows containment): terminate every process in the job with a deterministic
    /// exit code, then close the handle. Used on the supervisor's Kill/KillAll/TTL
    /// paths on top of the portable tree-kill. Idempotent with <see cref="Close"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public void TerminateAndClose()
    {
        lock (_gate)
        {
            if (_handle == nint.Zero)
                return;
            _ = TerminateJobObject(_handle, KilledExitCode);
            _ = CloseHandle(_handle);
            _handle = nint.Zero;
        }
    }

    /// <summary>
    /// Closes the job handle. With kill-on-close this alone terminates every process
    /// still in the job — the same thing the OS does for docketd on death. Used on the
    /// natural-exit cleanup path and to unwind a partly-built job. Idempotent.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public void Close()
    {
        lock (_gate)
        {
            if (_handle == nint.Zero)
                return;
            _ = CloseHandle(_handle);
            _handle = nint.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    // lpJobAttributes and lpName are always null here: an unnamed job with default
    // security. Typed as nint so no string marshalling is generated — fully blittable.
    [LibraryImport("kernel32", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, nint lpName);

    [LibraryImport("kernel32", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint hJob, int jobObjectInformationClass,
        in JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [LibraryImport("kernel32", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint hJob, uint uExitCode);

    [LibraryImport("kernel32", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        nint processHandle, nint jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
