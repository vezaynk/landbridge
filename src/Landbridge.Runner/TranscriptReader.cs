using System.Text;
using Landbridge.Contracts;

namespace Landbridge.Runner;

/// <summary>
/// Answers one <see cref="ReadTranscriptCommand"/> with one
/// <see cref="TranscriptChunkEvent"/> (spec §12 serving). Pure and synchronous: it reads
/// a byte range off local disk and shapes the reply, holding no per-request state — the
/// plane's cursor is the whole conversation, so a landbridged restart mid-read costs the
/// operator a refresh and nothing else.
///
/// <para><b>Verbatim.</b> The bytes go out exactly as captured. Landbridge does not redact
/// transcripts — how to do that well is unresolved (§13, §16 open question 8) — so a
/// transcript may carry credentials the agent echoed, and the compensating controls live
/// on the plane: a human operator session only, and only for a task in a terminal state,
/// whose worker instance token is already revoked. This class deliberately contains no
/// filtering to be mistaken for a boundary.</para>
///
/// <para><b>Flow control is the caller's cursor, not a buffer here.</b> One command, one
/// reply, at most <see cref="ReadTranscriptCommand.MaxBytes"/> in flight (§10 channel
/// separation). Nothing is cached, so a slow reader costs this machine nothing but the
/// next seek.</para>
/// </summary>
public sealed class TranscriptReader(TranscriptStore store)
{
    /// <summary>The longest UTF-8 encoding of one character, and therefore the floor on
    /// how many bytes a range reads (see <see cref="Read"/>).</summary>
    private const int MaxUtf8SequenceLength = 4;

    /// <summary>
    /// The ceiling on one range, whatever the caller asked for: four times the wire
    /// default (<see cref="TranscriptStreams.DefaultMaxBytes"/>), which leaves room to
    /// tune the plane's chunk size without a runner change while keeping a single frame's
    /// allocation — and its occupancy of the control socket — bounded (§10).
    /// </summary>
    internal const int MaxRangeBytes = 4 * TranscriptStreams.DefaultMaxBytes;


    /// <summary>
    /// Reads the range the command names, or shapes a refusal. Never throws: an I/O
    /// failure is <see cref="TranscriptRefusals.ReadFailed"/>, because a transcript read
    /// is an operator convenience and must not be able to take a command handler down.
    /// </summary>
    public TranscriptChunkEvent Read(ReadTranscriptCommand command)
    {
        if (command.Ordinal < 0 || command.Offset < 0 || command.MaxBytes < 1 ||
            !TranscriptStreams.IsKnown(command.Stream))
        {
            return Refuse(command, TranscriptRefusals.BadRequest);
        }

        return command.Ordinal == 0 ? Inventory(command) : ReadRange(command);
    }

    private TranscriptChunkEvent Inventory(ReadTranscriptCommand command)
    {
        var instances = store.Inventory(command.Session);
        return instances is null
            ? Refuse(command, TranscriptRefusals.NoTranscript)
            : new TranscriptChunkEvent(command.Session, command.RequestId, Instances: instances, Eof: true);
    }

    private TranscriptChunkEvent ReadRange(ReadTranscriptCommand command)
    {
        var path = store.ResolveFile(command.Session, command.Ordinal, command.Stream);
        if (path is null)
        {
            // Distinguish "nothing for this task here" from "that instance/stream was
            // never captured" — the first is a machine mismatch or a completed prune,
            // the second is a real task with one silent stream (§12).
            return Refuse(
                command,
                store.Holds(command.Session) ? TranscriptRefusals.UnknownInstance : TranscriptRefusals.NoTranscript);
        }

        try
        {
            // FileShare.ReadWrite is load-bearing: a capture writer may still hold this
            // file open for append. The plane only serves terminal tasks, but `canceled`
            // is marked before the harness has finished winding down (§11 stop TTL), so a
            // live writer is a real case, and Windows enforces share modes.
            using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (command.Offset >= file.Length)
            {
                // Past the end: not an error, just nothing more to send. Reported at the
                // offset asked for so the caller's cursor does not move backwards.
                return new TranscriptChunkEvent(
                    command.Session, command.RequestId, NextOffset: command.Offset, Eof: true);
            }

            file.Seek(command.Offset, SeekOrigin.Begin);

            // MaxBytes is a number off the wire, and it decides an allocation on this
            // machine — so it is clamped at both ends rather than trusted. The floor is
            // the longest UTF-8 sequence: a range smaller than the character straddling
            // its end could not carry that character in any chunk, leaving the trim below
            // to choose between corrupting it and stalling the cursor, and four bytes is
            // immaterial to what MaxBytes exists to bound. The ceiling keeps one
            // malformed int from turning a transcript read into an out-of-memory kill on a
            // machine that is busy running agents. A caller wanting more just asks again.
            var requested = Math.Clamp(command.MaxBytes, MaxUtf8SequenceLength, MaxRangeBytes);

            // And never allocate for more than the file can supply.
            var size = (int)Math.Min(requested, file.Length - command.Offset);
            var buffer = new byte[size];
            var read = file.ReadAtLeast(buffer, size, throwOnEndOfStream: false);

            // A range boundary can land inside a multi-byte character. Trim the trailing
            // partial sequence and leave it for the next read, so reassembling every
            // chunk reproduces the file byte-for-byte instead of sprinkling replacement
            // characters at each boundary.
            var usable = TrimPartialTrailingCharacter(buffer, read);

            var next = command.Offset + usable;
            return new TranscriptChunkEvent(
                command.Session,
                command.RequestId,
                Text: Encoding.UTF8.GetString(buffer, 0, usable),
                NextOffset: next,
                Eof: next >= file.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Refuse(command, TranscriptRefusals.ReadFailed);
        }
    }

    private static TranscriptChunkEvent Refuse(ReadTranscriptCommand command, string refusal) =>
        new(command.Session, command.RequestId, Refusal: refusal);

    /// <summary>
    /// The usable prefix of <paramref name="buffer"/>, excluding a trailing UTF-8
    /// sequence the range cut in half. Scans back at most three bytes to the last lead
    /// byte and drops it only when its sequence needs more bytes than are present.
    ///
    /// <para>Returns <paramref name="read"/> unchanged when the trim would empty a
    /// non-empty read — a cursor that cannot advance is worse than one replacement
    /// character. With the four-byte read floor this is unreachable for a well-formed
    /// file; it is the safety net for a malformed tail (a sequence the file itself cuts
    /// short at EOF), where advancing past the bad bytes is the only way to finish.</para>
    /// </summary>
    internal static int TrimPartialTrailingCharacter(byte[] buffer, int read)
    {
        // ASCII (0xxxxxxx) is never partial, which is the overwhelmingly common case for
        // a stream-json transcript, so this exits immediately for most reads.
        if (read == 0 || (buffer[read - 1] & 0x80) == 0)
            return read;

        for (var i = read - 1; i >= 0 && i >= read - 3; i--)
        {
            if ((buffer[i] & 0xC0) == 0x80)
                continue; // a continuation byte; keep walking back to the lead

            var needed = buffer[i] switch
            {
                var b when (b & 0xE0) == 0xC0 => 2,
                var b when (b & 0xF0) == 0xE0 => 3,
                var b when (b & 0xF8) == 0xF0 => 4,
                _ => 1, // not a valid lead byte: leave the bytes alone, the decoder substitutes
            };

            var trimmed = needed > read - i ? i : read;
            return trimmed == 0 ? read : trimmed;
        }

        // Four or more trailing continuation bytes is not valid UTF-8; hand it to the
        // decoder rather than guessing (it substitutes replacement characters).
        return read;
    }
}
