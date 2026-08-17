using System.Text;
using Docket.Contracts;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// The §12 serving half at the docketd seam: inventory, byte-range reads that reassemble
/// the file exactly, and the refusals that keep a bad request from becoming an exception.
/// No plane, no socket — <see cref="TranscriptReader"/> is pure over local files, which is
/// what makes the byte-exactness assertions here meaningful.
/// </summary>
public sealed class TranscriptReaderTests : IDisposable
{
    private readonly string _root = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));

    private TranscriptStore Store() => new(_root, TimeSpan.FromDays(7), _clock);

    private TranscriptReader Reader() => new(Store());

    /// <summary>Captures one instance of <paramref name="task"/> and returns its writer
    /// (already closed) so a test can read the paths it wrote.</summary>
    private TranscriptWriter Capture(SessionId task, string[] stdout, string[]? stderr = null)
    {
        var writer = Store().CreateWriter(task, TranscriptDefaults.MaxBytes);
        foreach (var line in stdout)
            writer.WriteStdoutLine(line);
        foreach (var line in stderr ?? [])
            writer.WriteStderrLine(line);
        writer.Dispose();
        return writer;
    }

    private static ReadTranscriptCommand Range(
        SessionId task, int ordinal = 1, string stream = TranscriptStreams.Stdout,
        long offset = 0, int maxBytes = TranscriptStreams.DefaultMaxBytes) =>
        new(task, "req-1", ordinal, stream, offset, maxBytes);

    /// <summary>Drains a whole stream the way the plane does — one range at a time,
    /// following the cursor — and returns the reassembled text plus the chunk count.</summary>
    private (string Text, int Chunks) DrainStream(
        SessionId task, int ordinal, string stream, int maxBytes)
    {
        var reader = Reader();
        var sb = new StringBuilder();
        var offset = 0L;
        var chunks = 0;
        while (true)
        {
            var reply = reader.Read(Range(task, ordinal, stream, offset, maxBytes));
            Assert.Null(reply.Refusal);
            chunks++;
            sb.Append(reply.Text);
            // A cursor that does not advance would spin forever; the reader guarantees
            // progress while bytes remain, so assert it rather than loop on trust.
            Assert.True(reply.Eof || reply.NextOffset > offset, "the cursor must advance while bytes remain");
            offset = reply.NextOffset;
            if (reply.Eof)
                return (sb.ToString(), chunks);
            Assert.True(chunks < 10_000, "drain did not terminate");
        }
    }

    public void Dispose() => TestKit.TryDeleteRoot(_root);

    // ── Inventory ─────────────────────────────────────────────────────────────

    [Fact]
    public void Inventory_lists_every_captured_instance_with_its_stream_sizes()
    {
        var task = SessionId.New();
        Capture(task, ["first instance stdout"], ["first instance stderr"]);
        Capture(task, ["second instance stdout only"]);

        var reply = Reader().Read(new ReadTranscriptCommand(task, "req-1"));

        Assert.Null(reply.Refusal);
        Assert.NotNull(reply.Instances);
        Assert.Collection(
            reply.Instances!,
            first =>
            {
                Assert.Equal(1, first.Ordinal);
                Assert.Equal("first instance stdout\n".Length, first.StdoutBytes);
                Assert.Equal("first instance stderr\n".Length, first.StderrBytes);
            },
            second =>
            {
                Assert.Equal(2, second.Ordinal);
                Assert.Equal("second instance stdout only\n".Length, second.StdoutBytes);
                // A silent stream never opened a file, so it has no bytes rather than
                // an empty file (capture opens lazily).
                Assert.Equal(0, second.StderrBytes);
            });
    }

    [Fact]
    public void Inventory_of_a_task_this_machine_never_ran_is_refused()
    {
        // No dir at all: never ran here, capture was off, or the prune sweep took it.
        // One answer for all three — they are indistinguishable after the fact (§12).
        var reply = Reader().Read(new ReadTranscriptCommand(SessionId.New(), "req-1"));

        Assert.Equal(TranscriptRefusals.NoTranscript, reply.Refusal);
        Assert.Null(reply.Instances);
    }

    [Fact]
    public void Inventory_of_a_task_that_captured_nothing_is_an_empty_list_not_a_refusal()
    {
        // The dir exists (the worker was dispatched here) but both streams stayed silent.
        // Distinct from the no-dir case: it ran here and produced nothing.
        var task = SessionId.New();
        Store().CreateWriter(task, TranscriptDefaults.MaxBytes).Dispose();

        var reply = Reader().Read(new ReadTranscriptCommand(task, "req-1"));

        Assert.Null(reply.Refusal);
        Assert.Empty(reply.Instances!);
    }

    // ── Ranged reads ──────────────────────────────────────────────────────────

    [Fact]
    public void A_single_range_returns_the_whole_stream_verbatim()
    {
        // Verbatim is the product behavior, not an accident: a planted credential comes
        // back intact, which is exactly why the plane serves this to human operators only
        // and only for terminal tasks (§13, §16 open question 8).
        var task = SessionId.New();
        var writer = Capture(task, ["""{"type":"assistant","text":"token dkt_w_deadbeef"}"""]);

        var reply = Reader().Read(Range(task));

        Assert.Null(reply.Refusal);
        Assert.Equal(File.ReadAllText(writer.StdoutPath), reply.Text);
        Assert.Contains("dkt_w_deadbeef", reply.Text);
        Assert.True(reply.Eof);
    }

    [Fact]
    public void Chunked_reads_reassemble_the_file_byte_for_byte()
    {
        var task = SessionId.New();
        var lines = Enumerable.Range(0, 200)
            .Select(i => $"{{\"type\":\"assistant\",\"seq\":{i},\"text\":\"line {i} of the transcript\"}}")
            .ToArray();
        var writer = Capture(task, lines);

        // 64-byte ranges force many chunks over a ~9 KB file, including boundaries that
        // land mid-line — the reader is byte-oriented and must not care.
        var (text, chunks) = DrainStream(task, 1, TranscriptStreams.Stdout, maxBytes: 64);

        Assert.True(chunks > 100, $"expected many chunks, got {chunks}");
        Assert.Equal(File.ReadAllText(writer.StdoutPath), text);
    }

    [Fact]
    public void A_range_boundary_inside_a_multi_byte_character_never_corrupts_it()
    {
        // The hazard: a 3-byte range over "é" (2 bytes) or "🛠" (4 bytes) splits a
        // character. The reader trims the partial trailing sequence and leaves it for the
        // next range, so reassembly is exact and no replacement character appears.
        var task = SessionId.New();
        var writer = Capture(task, ["""{"text":"aé🛠b — naïve ✓ résumé 日本語"}""", """{"text":"🛠🛠🛠"}"""]);

        foreach (var maxBytes in new[] { 1, 2, 3, 4, 5, 7 })
        {
            var (text, _) = DrainStream(task, 1, TranscriptStreams.Stdout, maxBytes);

            Assert.Equal(File.ReadAllText(writer.StdoutPath), text);
            Assert.DoesNotContain('�', text);
        }
    }

    [Fact]
    public void A_range_larger_than_the_ceiling_is_clamped_rather_than_allocated()
    {
        // MaxBytes arrives over the wire and decides an allocation here, so an absurd
        // value must be clamped, not honoured — one malformed int should not be able to
        // OOM a machine that is busy running agents.
        var task = SessionId.New();
        // ~1.4 MiB, comfortably over the ceiling, so the first range cannot be the whole file.
        Capture(task, Enumerable.Range(0, 20_000).Select(i => $"line {i} padded out to about seventy bytes of transcript text").ToArray());

        var reply = Reader().Read(Range(task, maxBytes: int.MaxValue));

        Assert.Null(reply.Refusal);
        Assert.False(reply.Eof);
        Assert.True(
            Encoding.UTF8.GetByteCount(reply.Text) <= TranscriptReader.MaxRangeBytes,
            "a single range must not exceed the ceiling");

        // And the clamp does not strand the rest: following the cursor still drains it.
        var (text, chunks) = DrainStream(task, 1, TranscriptStreams.Stdout, maxBytes: int.MaxValue);
        var path = Store().ResolveFile(task, 1, TranscriptStreams.Stdout)!;

        Assert.True(chunks > 1);
        Assert.Equal(new FileInfo(path).Length, Encoding.UTF8.GetByteCount(text));
    }

    [Fact]
    public void Stderr_is_a_separate_stream()
    {
        var task = SessionId.New();
        var writer = Capture(task, ["stdout content"], ["stderr content", "a warning"]);

        var reply = Reader().Read(Range(task, stream: TranscriptStreams.Stderr));

        Assert.Equal(File.ReadAllText(writer.StderrPath), reply.Text);
        Assert.DoesNotContain("stdout content", reply.Text);
    }

    [Fact]
    public void An_offset_at_or_past_the_end_is_eof_with_no_bytes()
    {
        var task = SessionId.New();
        var writer = Capture(task, ["short"]);
        var length = new FileInfo(writer.StdoutPath).Length;

        var atEnd = Reader().Read(Range(task, offset: length));
        var pastEnd = Reader().Read(Range(task, offset: length + 4096));

        foreach (var reply in new[] { atEnd, pastEnd })
        {
            // Not an error — just nothing more. The cursor is reported where it was asked
            // for, so a caller polling a finished stream never moves backwards.
            Assert.Null(reply.Refusal);
            Assert.Equal("", reply.Text);
            Assert.True(reply.Eof);
        }
        Assert.Equal(length, atEnd.NextOffset);
    }

    [Fact]
    public void A_live_transcript_is_readable_while_its_writer_still_holds_the_file()
    {
        // `canceled` is terminal the moment the plane records it, but the harness winds
        // down over the stop TTL (§11), so serving can race a live capture writer. The
        // reader must open share-compatibly (Windows enforces share modes).
        var task = SessionId.New();
        var writer = Store().CreateWriter(task, TranscriptDefaults.MaxBytes);
        writer.WriteStdoutLine("while still running");

        var reply = Reader().Read(Range(task));

        Assert.Null(reply.Refusal);
        Assert.Equal("while still running\n", reply.Text);

        // And a later range picks up what was appended after the first read.
        writer.WriteStdoutLine("appended after the read");
        var next = Reader().Read(Range(task, offset: reply.NextOffset));
        Assert.Equal("appended after the read\n", next.Text);

        writer.Dispose();
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public void An_ordinal_that_was_never_captured_is_refused_as_an_unknown_instance()
    {
        var task = SessionId.New();
        Capture(task, ["only one instance"]);

        var reply = Reader().Read(Range(task, ordinal: 7));

        // The task ran here — that is the difference from no-transcript.
        Assert.Equal(TranscriptRefusals.UnknownInstance, reply.Refusal);
    }

    [Fact]
    public void A_stream_that_stayed_silent_is_refused_as_an_unknown_instance()
    {
        var task = SessionId.New();
        Capture(task, ["stdout only"]);

        var reply = Reader().Read(Range(task, stream: TranscriptStreams.Stderr));

        Assert.Equal(TranscriptRefusals.UnknownInstance, reply.Refusal);
    }

    [Fact]
    public void A_range_read_for_a_task_this_machine_never_ran_is_refused()
    {
        var reply = Reader().Read(Range(SessionId.New()));

        Assert.Equal(TranscriptRefusals.NoTranscript, reply.Refusal);
    }

    [Theory]
    [InlineData("stdout.ndjson")]
    [InlineData("STDOUT")]
    [InlineData("")]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\credentials.json")]
    public void An_unknown_stream_name_is_refused_and_never_reaches_the_filesystem(string stream)
    {
        // Path construction is closed — the stream name only ever selects a known
        // extension — so a traversal attempt is a vocabulary error, not a path to sanitize.
        var task = SessionId.New();
        Capture(task, ["content"]);

        var reply = Reader().Read(Range(task, stream: stream));

        Assert.Equal(TranscriptRefusals.BadRequest, reply.Refusal);
        Assert.Equal("", reply.Text);
    }

    [Theory]
    [InlineData(-1, 0, 4096)]
    [InlineData(1, -1, 4096)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 0, -4096)]
    public void A_malformed_range_is_refused_rather_than_throwing(int ordinal, long offset, int maxBytes)
    {
        var task = SessionId.New();
        Capture(task, ["content"]);

        var reply = Reader().Read(Range(task, ordinal, offset: offset, maxBytes: maxBytes));

        Assert.Equal(TranscriptRefusals.BadRequest, reply.Refusal);
    }

    [Fact]
    public void Every_reply_carries_the_request_id_and_task_back()
    {
        // The plane correlates a reply to the range it asked for by request id alone, so
        // it must survive every shape — inventory, range, and refusal.
        var task = SessionId.New();
        Capture(task, ["content"]);
        var reader = Reader();

        RunnerEvent[] replies =
        [
            reader.Read(new ReadTranscriptCommand(task, "req-inventory")),
            reader.Read(Range(task) with { RequestId = "req-range" }),
            reader.Read(Range(task, ordinal: 99) with { RequestId = "req-refusal" }),
        ];

        Assert.Collection(
            replies.Cast<TranscriptChunkEvent>(),
            inventory => Assert.Equal("req-inventory", inventory.RequestId),
            range => Assert.Equal("req-range", range.RequestId),
            refusal => Assert.Equal("req-refusal", refusal.RequestId));
        Assert.All(replies.Cast<TranscriptChunkEvent>(), r => Assert.Equal(task, r.Session));
    }
}
