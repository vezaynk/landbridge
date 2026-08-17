using Landbridge.Core;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Runner.Tests;

/// <summary>
/// The §12 machine-local transcript capture store, writer, and pruner in isolation
/// (no spawned process): verbatim line capture, the per-stream size cap with its
/// truncation marker, per-instance file naming, and the local-hygiene sweep. Driven by
/// <see cref="FakeTimeProvider"/> so pruning is deterministic.
/// </summary>
public sealed class TranscriptStoreTests : IDisposable
{
    private readonly string _root = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));

    private TranscriptStore Store(TimeSpan? retention = null) =>
        new(_root, retention ?? TimeSpan.FromDays(7), _clock);

    [Fact]
    public async Task Writer_writes_stdout_and_stderr_lines_verbatim()
    {
        var task = SessionId.New();
        var writer = Store().CreateWriter(task, TranscriptDefaults.MaxBytes);

        string[] stdout =
        [
            """{"type":"system","subtype":"init","session_id":"s1"}""",
            """{"type":"assistant","message":{"content":[]}}""",
            "a non-JSON banner the harness printed",
        ];
        string[] stderr = ["boot warning", "another diagnostic"];

        foreach (var line in stdout)
            writer.WriteStdoutLine(line);
        foreach (var line in stderr)
            writer.WriteStderrLine(line);
        writer.Dispose();

        Assert.Equal(stdout, await File.ReadAllLinesAsync(writer.StdoutPath));
        Assert.Equal(stderr, await File.ReadAllLinesAsync(writer.StderrPath));
    }

    [Fact]
    public void Writer_creates_no_files_for_a_silent_stream()
    {
        // Files open lazily on the first line, so a worker that writes nothing to a
        // stream leaves no empty transcript file.
        var task = SessionId.New();
        var writer = Store().CreateWriter(task, TranscriptDefaults.MaxBytes);
        writer.WriteStdoutLine("only stdout here");
        writer.Dispose();

        Assert.True(File.Exists(writer.StdoutPath));
        Assert.False(File.Exists(writer.StderrPath));
    }

    [Fact]
    public async Task Writer_stops_at_the_size_cap_with_a_truncation_marker()
    {
        var task = SessionId.New();
        // Each line is 10 chars + '\n' = 11 bytes. A 40-byte cap admits three lines
        // (33 bytes); the fourth would cross it, so a marker lands instead and writing
        // stops — later lines are dropped, never written.
        var writer = Store().CreateWriter(task, maxBytes: 40);

        writer.WriteStdoutLine("0123456789");
        writer.WriteStdoutLine("1123456789");
        writer.WriteStdoutLine("2123456789");
        writer.WriteStdoutLine("3123456789"); // crosses the cap → marker + stop
        writer.WriteStdoutLine("SHOULD_NOT_APPEAR");
        writer.Dispose();

        var lines = await File.ReadAllLinesAsync(writer.StdoutPath);
        Assert.Equal(4, lines.Length);
        Assert.Equal("0123456789", lines[0]);
        Assert.Equal("2123456789", lines[2]);
        Assert.Contains("transcript_truncated", lines[3]);
        Assert.DoesNotContain(lines, l => l.Contains("SHOULD_NOT_APPEAR"));
    }

    [Fact]
    public async Task Store_assigns_a_new_ordinal_per_instance_and_leaves_the_prior_intact()
    {
        var task = SessionId.New();
        var store = Store();

        var first = store.CreateWriter(task, TranscriptDefaults.MaxBytes);
        first.WriteStdoutLine("first-instance");
        first.Dispose();

        var second = store.CreateWriter(task, TranscriptDefaults.MaxBytes);
        second.WriteStdoutLine("second-instance");
        second.Dispose();

        Assert.EndsWith("0001.ndjson", first.StdoutPath);
        Assert.EndsWith("0002.ndjson", second.StdoutPath);
        Assert.NotEqual(first.StdoutPath, second.StdoutPath);
        // The predecessor's file is untouched by the successor instance.
        Assert.Equal(["first-instance"], await File.ReadAllLinesAsync(first.StdoutPath));
        Assert.Equal(["second-instance"], await File.ReadAllLinesAsync(second.StdoutPath));
    }

    [Fact]
    public void Prune_removes_stale_task_dirs_and_keeps_recent_ones()
    {
        var store = Store(TimeSpan.FromDays(7));

        var stale = SessionId.New();
        var staleWriter = store.CreateWriter(stale, TranscriptDefaults.MaxBytes);
        staleWriter.WriteStdoutLine("old work");
        staleWriter.Dispose();

        var recent = SessionId.New();
        var recentWriter = store.CreateWriter(recent, TranscriptDefaults.MaxBytes);
        recentWriter.WriteStdoutLine("recent work");
        recentWriter.Dispose();

        var now = _clock.GetUtcNow().UtcDateTime;
        Backdate(Path.Combine(_root, stale.ToString()), now.AddDays(-30));  // older than 7d
        Backdate(Path.Combine(_root, recent.ToString()), now.AddDays(-1));  // within 7d

        var removed = store.Prune();

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.Combine(_root, stale.ToString())));
        Assert.True(Directory.Exists(Path.Combine(_root, recent.ToString())));
    }

    [Fact]
    public void A_pruned_task_is_no_longer_inventoried()
    {
        // Retention is the whole story for how long a transcript is readable: the plane
        // stores no bytes and has no tier of its own, so once the local sweep takes a task
        // dir the §12 serving path can only report that nothing is held (§12).
        var store = Store(TimeSpan.FromDays(7));
        var task = SessionId.New();
        var writer = store.CreateWriter(task, TranscriptDefaults.MaxBytes);
        writer.WriteStdoutLine("work that will age out");
        writer.Dispose();

        Assert.NotNull(store.Inventory(task));

        Backdate(Path.Combine(_root, task.ToString()), _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        Assert.Equal(1, store.Prune());

        Assert.Null(store.Inventory(task));
        Assert.False(store.Holds(task));
        Assert.Null(store.ResolveFile(task, 1, TranscriptStreams.Stdout));
    }

    [Fact]
    public void Prune_is_disabled_when_retention_is_non_positive()
    {
        var store = Store(TimeSpan.Zero); // 0 (or negative) disables pruning entirely

        var task = SessionId.New();
        var writer = store.CreateWriter(task, TranscriptDefaults.MaxBytes);
        writer.WriteStdoutLine("ancient");
        writer.Dispose();
        Backdate(Path.Combine(_root, task.ToString()), _clock.GetUtcNow().UtcDateTime.AddDays(-3650));

        Assert.Equal(0, store.Prune());
        Assert.True(Directory.Exists(Path.Combine(_root, task.ToString())));
    }

    /// <summary>Backdate a task dir and every file in it so the pruner's "newest write"
    /// (max over the dir and its files) reflects the intended age.</summary>
    private static void Backdate(string dir, DateTime whenUtc)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            File.SetLastWriteTimeUtc(file, whenUtc);
        Directory.SetLastWriteTimeUtc(dir, whenUtc);
    }

    public void Dispose() => TestKit.TryDeleteRoot(_root);
}
