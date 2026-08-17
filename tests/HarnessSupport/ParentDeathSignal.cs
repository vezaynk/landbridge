using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Landbridge.HarnessSupport;

/// <summary>
/// The Linux half of the harness dead-man's switch (§10): arm
/// <c>prctl(PR_SET_PDEATHSIG, SIGKILL)</c> so the kernel SIGKILLs this process the
/// instant its parent — <c>landbridged</c>, its direct spawner — dies. Immediate, no
/// polling, no cooperation required, and it fires even when landbridged itself is
/// SIGKILLed.
///
/// This complements, and never replaces, the portable stdin-EOF watch each harness
/// runs: EOF covers every OS and a clean pipe close; PDEATHSIG makes the
/// landbridged→harness case instantaneous on Linux. It covers the <b>direct child
/// only</b>. Grandchildren the harness spawns are not signalled by this call — the
/// harness kills them itself on the EOF path, and landbridged's <c>StrayReaper</c> is
/// the restart backstop.
///
/// Shared by <c>Landbridge.Runner.TestHarness</c> and <c>Landbridge.WorkerHarness</c> as a
/// linked source file so both reference harnesses carry the identical convention a
/// BYO harness would copy. AOT-clean (<see cref="LibraryImportAttribute"/>,
/// blittable signatures).
///
/// Caveat worth knowing: the kernel keys PDEATHSIG off the <em>thread</em> that
/// forked this process, not the whole parent process. A spawner that forks from a
/// short-lived thread (e.g. a thread-pool thread that is later trimmed) can trip it
/// early. The stdin-EOF watch has no such footgun and remains the real guarantee.
/// </summary>
internal static partial class ParentDeathSignal
{
    // <linux/prctl.h>: PR_SET_PDEATHSIG == 1. SIGKILL == 9 on every Linux ABI.
    private const int PR_SET_PDEATHSIG = 1;
    private const int SIGKILL = 9;

    /// <summary>
    /// Arms PDEATHSIG on Linux; a silent no-op on macOS/Windows, so callers invoke
    /// it unconditionally at startup. Handles the classic race — the parent dying
    /// between our spawn and this call — by re-reading <c>getppid()</c>: if we have
    /// been reparented (the parent is already gone) the SIGKILL will never arrive,
    /// so we report that the caller should self-terminate now.
    /// </summary>
    /// <returns><c>true</c> when the caller should self-terminate immediately (the
    /// parent is already gone); otherwise <c>false</c>.</returns>
    /// <summary>
    /// Test-rig knob (harness-support code only, never production): a rig that
    /// plants a process tree and needs deterministic behaviour sets this to pin
    /// the tree to ONE mechanism. The portable-EOF test disables PDEATHSIG so the
    /// graceful stdin path always runs (on Linux the SIGKILL would otherwise
    /// preempt it and win the race); the stray-reap E2E disables it because its
    /// planted "stray" must SURVIVE its spawner (the whole point of a stray).
    /// Inherited by the whole planted tree via the environment.
    /// </summary>
    public const string DisableEnvVar = "LANDBRIDGE_TEST_DISABLE_PDEATHSIG";

    public static bool ArmAndParentAlreadyDead()
    {
        if (!OperatingSystem.IsLinux())
            return false;
        if (Environment.GetEnvironmentVariable(DisableEnvVar) is not null)
            return false;
        return ArmLinux();
    }

    [SupportedOSPlatform("linux")]
    private static bool ArmLinux()
    {
        var parentBefore = getppid();
        // Best-effort: if prctl fails we still have the portable stdin-EOF watch.
        _ = prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0);
        // If our parent changed out from under us during the arm, it exited before
        // PDEATHSIG took hold and the signal was already missed — bail out. (A
        // residual window remains if the parent died before this method ran at all;
        // the stdin-EOF watch covers that case.)
        return getppid() != parentBefore;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [LibraryImport("libc")]
    private static partial int getppid();
}
