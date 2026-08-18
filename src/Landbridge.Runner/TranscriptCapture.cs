using System.Globalization;
using System.Text;
using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.Runner;

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

    /// <summary>The stdout transcript's extension (harness stream-json, one object per line).</summary>
    public const string StdoutExtension = ".ndjson";

    /// <summary>The stderr transcript's extension (plain lines).</summary>
    public const string StderrExtension = ".stderr";
}

/// <summary>
/// Owns a machine's captured harness transcripts under <c>&lt;state&gt;/transcripts</c>
/// (spec §12). It writes the harness's own stream-json stdout and its stderr verbatim to
/// per-task, per-instance files so a failed task's work is recoverable and debuggable —
/// and, per §11, so the local transcript survives a reboot as the resume substrate. It
/// also answers the two questions the §12 serving path asks of the layout —
/// <see cref="Inventory"/> (what instances exist) and <see cref="ResolveFile"/> (which
/// file backs one) — leaving reading and framing to <see cref="TranscriptReader"/>.
///
/// <para>Retention here is machine-local hygiene only (<see cref="Prune"/>); the plane
/// keeps no transcript bytes and has no retention tier of its own (§12).</para>
///
/// <para><b>Layout.</b> <c>&lt;state&gt;/transcripts/&lt;task-id&gt;/&lt;NNNN&gt;.ndjson</c>
/// (stdout, one JSON object per line) and <c>&lt;NNNN&gt;.stderr</c> (stderr, plain
/// lines), where <c>NNNN</c> is a per-task instance ordinal. Each dispatch (a first
/// spawn, a requeue, a §11 resume) is a distinct worker instance and gets the next
/// ordinal, so a redispatch never clobbers its predecessor's transcript and the
/// ordinal is stable across a landbridged restart (it is derived by scanning the dir).</para>
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
    public TranscriptWriter CreateWriter(SessionId task, long maxBytes)
    {
        var dir = Path.Combine(_root, task.ToString());
        Directory.CreateDirectory(dir);
        SetDirOwnerOnly(_root);
        SetDirOwnerOnly(dir);

        var ordinal = NextOrdinal(dir);
        return new TranscriptWriter(
            PathFor(dir, ordinal, TranscriptDefaults.StdoutExtension),
            PathFor(dir, ordinal, TranscriptDefaults.StderrExtension),
            maxBytes);
    }

    /// <summary>
    /// What this machine holds for <paramref name="task"/> (§12 serving): one entry per
    /// captured worker instance, with each stream's on-disk size. Null when the task has
    /// no directory here at all — it never ran on this machine, capture was off for the
    /// profile that ran it, or the prune sweep has already removed it. Those are
    /// indistinguishable after the fact and deliberately one answer.
    ///
    /// <para>An <em>empty</em> list is a different, honest answer: the task ran here and
    /// captured nothing (files open lazily, so a silent worker leaves only the dir).</para>
    /// </summary>
    public IReadOnlyList<TranscriptInstance>? Inventory(SessionId task)
    {
        var dir = Path.Combine(_root, task.ToString());
        if (!Directory.Exists(dir))
            return null;

        var byOrdinal = new Dictionary<int, (long Stdout, long Stderr, DateTimeOffset LastWrite)>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (!TryReadOrdinal(file, out var ordinal))
                continue;

            var info = new FileInfo(file);
            long length;
            try
            {
                length = info.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // vanished or unreadable mid-scan; it simply isn't listed
            }

            byOrdinal.TryGetValue(ordinal, out var entry);
            var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            entry = Path.GetExtension(file) switch
            {
                TranscriptDefaults.StdoutExtension => (length, entry.Stderr, Newer(entry.LastWrite, lastWrite)),
                TranscriptDefaults.StderrExtension => (entry.Stdout, length, Newer(entry.LastWrite, lastWrite)),
                _ => entry,
            };
            byOrdinal[ordinal] = entry;
        }

        return byOrdinal
            .OrderBy(kv => kv.Key)
            .Select(kv => new TranscriptInstance(kv.Key, kv.Value.Stdout, kv.Value.Stderr, kv.Value.LastWrite))
            .ToArray();
    }

    /// <summary>
    /// The file backing one instance's stream, or null when that instance/stream was
    /// never captured (§12 serving). <paramref name="stream"/> is a
    /// <see cref="TranscriptStreams"/> name; anything else returns null rather than
    /// touching the filesystem. Path construction is closed by design — a
    /// <see cref="SessionId"/> is a Guid, the ordinal is formatted from an <c>int</c>, and
    /// the extension comes from the validated stream name — so nothing from the wire can
    /// steer the path outside the transcripts root.
    /// </summary>
    public string? ResolveFile(SessionId task, int ordinal, string stream)
    {
        if (ordinal < 1)
            return null;
        var extension = stream switch
        {
            TranscriptStreams.Stdout => TranscriptDefaults.StdoutExtension,
            TranscriptStreams.Stderr => TranscriptDefaults.StderrExtension,
            _ => null,
        };
        if (extension is null)
            return null;

        var path = PathFor(Path.Combine(_root, task.ToString()), ordinal, extension);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Whether this machine holds any transcript directory for the task.</summary>
    public bool Holds(SessionId task) => Directory.Exists(Path.Combine(_root, task.ToString()));

    private static DateTimeOffset Newer(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static string PathFor(string dir, int ordinal, string extension) =>
        Path.Combine(dir, ordinal.ToString("D4", CultureInfo.InvariantCulture) + extension);

    /// <summary>The instance ordinal encoded in a transcript file's name, or false when
    /// the name is not one of ours (both streams' stems are the ordinal).</summary>
    private static bool TryReadOrdinal(string file, out int ordinal) =>
        int.TryParse(
            Path.GetFileNameWithoutExtension(file),
            NumberStyles.None, CultureInfo.InvariantCulture, out ordinal);

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
            if (TryReadOrdinal(file, out var n) && n > max)
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
/// landbridged crash loses at most a partial final line, and the naming is append-safe.</para>
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
                // synthetic object line (discriminator `landbridge`, not `type`), and the
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

            // Share mode is load-bearing, do not "simplify" it away:
            //   FileMode.Append   — never truncate; a fresh ordinal is a fresh file, but
            //                       append is the no-clobber guarantee if a path is ever
            //                       reused across a restart.
            //   FileShare.Read    — a reader (an operator tailing, the future plane-side
            //                       serving) MUST be able to open this file WHILE we hold
            //                       it open for writing. Windows enforces share modes
            //                       (Unix does not), so without this a live read throws
            //                       "being used by another process". It still refuses a
            //                       second WRITER, keeping our write exclusive. Readers
            //                       must in turn open with FileShare.ReadWrite to tolerate
            //                       this live Write handle.
            var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _bytes = stream.Length; // correct accounting if appending to existing bytes
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true, // line-durable: a landbridged crash loses at most a partial line
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
            "{\"landbridge\":\"transcript_truncated\",\"limit_bytes\":"
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
