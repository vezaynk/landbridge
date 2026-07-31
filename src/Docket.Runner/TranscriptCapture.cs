using System.Globalization;
using System.Text;
using Docket.Core;

namespace Docket.Runner;

/// <summary>Defaults for machine-local transcript capture (§12), shared by the
/// config record's parameter defaults and <see cref="RunnerConfig"/> validation so
/// a direct <see cref="LogsConfig"/> construction and a parsed config agree.</summary>
public static class TranscriptDefaults
{
    /// <summary>Per-stream, per-instance size cap before truncation (§12). 50 MiB.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>Local hygiene: remove a task's transcript dir this many days after its
    /// last write (§12 capture — NOT the plane's §12 retention tiers). 0 disables.</summary>
    public const int PruneAfterDays = 7;

    /// <summary>The state-dir subdirectory transcripts live under.</summary>
    public const string DirName = "transcripts";
}

/// <summary>
/// Owns a machine's captured harness transcripts under <c>&lt;state&gt;/transcripts</c>
/// (spec §12 capture). This is the honest-capture half only: it writes the harness's
/// own stream-json stdout and its stderr verbatim to per-task, per-instance files so a
/// failed task's work is recoverable and debuggable — and, per §11, so the local
/// transcript survives a reboot as the resume substrate. Retention tiers, redaction,
/// and serving to the plane are a later plane-side increment; nothing here leaves the
/// machine.
///
/// <para><b>Layout.</b> <c>&lt;state&gt;/transcripts/&lt;task-id&gt;/&lt;NNNN&gt;.ndjson</c>
/// (stdout, one JSON object per line) and <c>&lt;NNNN&gt;.stderr</c> (stderr, plain
/// lines), where <c>NNNN</c> is a per-task instance ordinal. Each dispatch (a first
/// spawn, a requeue, a §11 resume) is a distinct worker instance and gets the next
/// ordinal, so a redispatch never clobbers its predecessor's transcript and the
/// ordinal is stable across a docketd restart (it is derived by scanning the dir).</para>
///
/// <para><b>Permissions.</b> A transcript can capture credentials an agent echoed (§13),
/// so the root, each task dir, and each file are owner-only (0700/0600), exactly like
/// the machine <c>credentials.json</c> and the worker <c>mcp.json</c>.</para>
/// </summary>
public sealed class TranscriptStore
{
    private readonly string _root;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _clock;

    /// <param name="root">The transcripts root, e.g. <c>&lt;state&gt;/transcripts</c>.</param>
    /// <param name="retention">Prune a task dir this long after its newest write.
    /// <see cref="TimeSpan.Zero"/> or negative disables pruning (keep everything).</param>
    public TranscriptStore(string root, TimeSpan retention, TimeProvider clock)
    {
        _root = root;
        _retention = retention;
        _clock = clock;
    }

    public string Root => _root;

    /// <summary>
    /// Opens a writer for a new worker instance of <paramref name="task"/>. The
    /// instance ordinal is the next one free in the task dir (max existing + 1), so
    /// concurrent-safe within a task (dispatches for one task are sequential) and
    /// restart-safe (the dir persists across restarts). Files are created lazily on
    /// first write, so a silent worker leaves no empty files.
    /// </summary>
    public TranscriptWriter CreateWriter(TaskId task, long maxBytes)
    {
        var dir = Path.Combine(_root, task.ToString());
        Directory.CreateDirectory(dir);
        SetDirOwnerOnly(_root);
        SetDirOwnerOnly(dir);

        var ordinal = NextOrdinal(dir);
        var stem = ordinal.ToString("D4", CultureInfo.InvariantCulture);
        return new TranscriptWriter(
            Path.Combine(dir, stem + ".ndjson"),
            Path.Combine(dir, stem + ".stderr"),
            maxBytes);
    }

    /// <summary>
    /// Machine-local hygiene sweep (§12 capture): remove any task dir whose newest
    /// file is older than the retention window, so a long-lived machine does not fill
    /// its disk. Best-effort — a dir that cannot be read or deleted is skipped, never
    /// fatal. Returns the number of dirs removed (for diagnostics/tests). A
    /// non-positive retention disables pruning entirely.
    /// </summary>
    public int Prune()
    {
        if (_retention <= TimeSpan.Zero || !Directory.Exists(_root))
            return 0;

        var cutoff = _clock.GetUtcNow() - _retention;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            try
            {
                if (NewestWriteUtc(dir) < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A racing writer, a permission quirk, a vanished dir — skip it.
            }
        }
        return removed;
    }

    /// <summary>The newest last-write time among a task dir's files, or the dir's own
    /// when it holds none — so an empty dir still ages out rather than lingering.</summary>
    private static DateTimeOffset NewestWriteUtc(string dir)
    {
        var newest = Directory.GetLastWriteTimeUtc(dir);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var t = File.GetLastWriteTimeUtc(file);
            if (t > newest)
                newest = t;
        }
        return newest;
    }

    /// <summary>Next instance ordinal: one past the highest <c>NNNN</c> already present
    /// (either stream's extension), or 1 for a fresh task dir.</summary>
    private static int NextOrdinal(string dir)
    {
        var max = 0;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max)
                max = n;
        }
        return max + 1;
    }

    internal static void SetDirOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// <summary>
/// The two capture files for one worker instance (spec §12 capture): the stdout
/// transcript (<c>.ndjson</c>) and stderr (<c>.stderr</c>). Both are written verbatim,
/// line by line, with a per-stream size cap. Reaching the cap writes a single
/// truncation marker line and stops writing that stream — it never blocks or kills the
/// worker, because logging is not allowed to affect the task (the caller keeps draining
/// the pipe past the cap; only the file write stops).
///
/// <para>Thread-safe: stdout and stderr are drained by separate pumps, so writes are
/// serialized under one lock. Files open lazily on first write and are owner-only
/// (0600) since a transcript can carry credentials (§13). Writes auto-flush so a
/// docketd crash loses at most a partial final line, and the naming is append-safe.</para>
/// </summary>
public sealed class TranscriptWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly CaptureStream _stdout;
    private readonly CaptureStream _stderr;
    private bool _disposed;

    internal TranscriptWriter(string stdoutPath, string stderrPath, long maxBytes)
    {
        _stdout = new CaptureStream(stdoutPath, maxBytes);
        _stderr = new CaptureStream(stderrPath, maxBytes);
    }

    /// <summary>The stdout transcript path (created lazily on first line).</summary>
    public string StdoutPath => _stdout.Path;

    /// <summary>The stderr transcript path (created lazily on first line).</summary>
    public string StderrPath => _stderr.Path;

    /// <summary>Append one verbatim stdout line (the harness stream-json transcript).</summary>
    public void WriteStdoutLine(string line)
    {
        lock (_gate)
        {
            if (!_disposed)
                _stdout.Write(line);
        }
    }

    /// <summary>Append one verbatim stderr line.</summary>
    public void WriteStderrLine(string line)
    {
        lock (_gate)
        {
            if (!_disposed)
                _stderr.Write(line);
        }
    }

    /// <summary>Flush and close both files. Idempotent; called once both feeding pumps end.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stdout.Close();
            _stderr.Close();
        }
    }

    /// <summary>One capture file with a byte budget and a one-shot truncation marker.</summary>
    private sealed class CaptureStream(string path, long maxBytes)
    {
        private StreamWriter? _writer;
        private long _bytes;
        private bool _truncated;

        public string Path { get; } = path;

        public void Write(string line)
        {
            if (_truncated)
                return;

            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1; // + '\n'
            if (_bytes + lineBytes > maxBytes)
            {
                // At the cap: emit one marker, then stop. A stream-json reader sees a
                // synthetic object line (discriminator `docket`, not `type`), and the
                // marker is greppable in a plain stderr file too.
                EnsureOpen();
                _writer!.Write(TruncationMarker(maxBytes));
                _writer.Write('\n');
                _writer.Flush();
                _truncated = true;
                return;
            }

            EnsureOpen();
            _writer!.Write(line);
            _writer.Write('\n');
            _bytes += lineBytes;
        }

        private void EnsureOpen()
        {
            if (_writer is not null)
                return;

            // Append, never truncate: a fresh ordinal means a fresh file, but append is
            // the no-clobber guarantee if a path is ever reused across a restart.
            var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _bytes = stream.Length; // correct accounting if appending to existing bytes
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true, // line-durable: a docketd crash loses at most a partial line
            };
            SetOwnerOnly(Path);
        }

        public void Close()
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }

        private static string TruncationMarker(long limit) =>
            "{\"docket\":\"transcript_truncated\",\"limit_bytes\":"
            + limit.ToString(CultureInfo.InvariantCulture) + "}";

        private static void SetOwnerOnly(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>Shared line-drain used for transcript capture (spec §12 capture).</summary>
public static class TranscriptCapture
{
    /// <summary>
    /// Drains <paramref name="reader"/> line-by-line to EOF, handing each verbatim line
    /// to <paramref name="onLine"/>. Same robustness contract as the terminal event
    /// drain: it never throws into its host task on cancellation or a torn-down pipe —
    /// a redirected stream MUST be drained for the process's whole lifetime or a full OS
    /// pipe buffer blocks the worker's writes, so this loop keeps reading even after the
    /// sink stops writing (e.g. past the size cap).
    /// </summary>
    public static async Task PumpLinesAsync(TextReader reader, Action<string> onLine, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                onLine(line);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Cancelled on teardown, or the pipe was closed by a kill mid-read — the
            // stream is over; end the drain quietly.
        }
    }
}
