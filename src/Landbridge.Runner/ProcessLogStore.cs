namespace Landbridge.Runner;

/// <summary>
/// Machine-local log capture for agent-started processes (§10, §12), deliberately in its
/// <b>own root</b> — <c>&lt;state&gt;/processes/&lt;name&gt;/</c> — rather than under the
/// transcripts root.
///
/// <para><b>Why a separate root, and it is not tidiness.</b>
/// <see cref="TranscriptStore.Prune"/> walks every top-level directory under the
/// transcripts root and deletes any whose newest write is older than the retention
/// window. That is safe for tasks, because a terminal task never writes again. It is
/// unsafe for a process: one that sits idle longer than the window — a dev server over
/// a weekend with the 7-day default — would have its <em>live</em> directory unlinked
/// from under an open append handle. Keeping process logs outside the pruned root means
/// the two lifetimes can never interact.</para>
///
/// <para>The write path itself is shared verbatim with transcripts:
/// <see cref="TranscriptWriter"/> supplies the per-stream byte cap, the single
/// truncation marker, lazy 0600 creation, and the guarantee that capture never blocks
/// or kills the process it is recording.</para>
///
/// <para>Reading these bytes back is deliberately <b>not</b> built. §12's read path
/// gates on a task being terminal, which is the compensating control for serving
/// unredacted output; a process is never terminal, so serving its log would be the live
/// tailing that §16 open question 8 defers pending redaction. Capture writes to disk,
/// the operator reads the file on the machine, and status reaches the dashboard through
/// the heartbeat instead.</para>
/// </summary>
public sealed class ProcessLogStore
{
    /// <summary>Directory name under the state dir; a sibling of <c>transcripts</c>.</summary>
    public const string DirName = "processes";

    private readonly string _root;

    /// <param name="root">The process log root, e.g. <c>&lt;state&gt;/processes</c>.</param>
    public ProcessLogStore(string root) => _root = root;

    public string Root => _root;

    /// <summary>
    /// Opens a writer for one run of <paramref name="processName"/>. Each start gets
    /// the next ordinal in the process's directory, so a restart never clobbers the log
    /// that recorded why the previous run died — which is the whole reason an operator
    /// looks here.
    /// </summary>
    /// <remarks>
    /// <paramref name="processName"/> must already have passed
    /// <see cref="RunnerConfig.IsValidProcessName"/>. That check is what
    /// keeps this path construction closed: it occupies the slot a <c>SessionId</c> Guid
    /// fills for transcripts, and a Guid is the reason that builder could be called
    /// safe. Never call this with a name from any other source.
    /// </remarks>
    public TranscriptWriter CreateWriter(string processName, long maxBytes)
    {
        if (!RunnerConfig.IsValidProcessName(processName))
            throw new ArgumentException(
                $"'{processName}' is not a valid process name; it would escape the log root",
                nameof(processName));

        var dir = Path.Combine(_root, processName);
        Directory.CreateDirectory(dir);
        TranscriptStore.SetDirOwnerOnly(_root);
        TranscriptStore.SetDirOwnerOnly(dir);

        var ordinal = NextOrdinal(dir);
        return new TranscriptWriter(
            Path.Combine(dir, ordinal.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + TranscriptDefaults.StdoutExtension),
            Path.Combine(dir, ordinal.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + TranscriptDefaults.StderrExtension),
            maxBytes);
    }

    private static int NextOrdinal(string dir)
    {
        var highest = 0;
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(stem, out var n) && n > highest)
                highest = n;
        }
        return highest + 1;
    }
}
