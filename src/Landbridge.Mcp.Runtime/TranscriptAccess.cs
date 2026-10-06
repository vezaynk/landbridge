using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.Core;
using Microsoft.AspNetCore.Http;

namespace Landbridge.Mcp;

/// <summary>
/// One transcript range, either from the in-process relay (tests, and Core itself) or
/// from Core over HTTP. Façades have an empty runner registry once <c>/runner</c> lives
/// on Core, so a dashboard or Lead tool that asked locally would report every machine
/// offline. When <see cref="CoreWriteClient"/> is enabled the read goes there and Core
/// talks to the machine. The bytes are not logged on either hop.
/// </summary>
public static class TranscriptAccess
{
    public static async Task<TranscriptResult> ReadAsync(
        TranscriptRelayService? local,
        CoreWriteClient? core,
        string? bearer,
        SessionId task,
        Guid machine,
        int ordinal,
        string stream,
        long offset,
        int maxBytes,
        CancellationToken ct)
    {
        if (core is { Enabled: true } && !string.IsNullOrEmpty(bearer))
        {
            var path =
                $"/core/v1/sessions/{task.Value:D}/transcript?machine={machine:D}&ordinal={ordinal}" +
                $"&stream={Uri.EscapeDataString(stream)}&offset={offset}&maxBytes={maxBytes}";
            var body = await core.GetAsync<CoreTranscriptReply>(path, bearer, ct);
            return Map(body, machine);
        }

        if (local is null)
        {
            return new TranscriptResult.Unavailable(
                TranscriptUnavailable.Timeout,
                "Transcript reads are not available on this host.");
        }

        return ordinal == 0
            ? await local.ListAsync(task, machine, ct)
            : await local.ReadAsync(task, machine, ordinal, stream, offset, maxBytes, ct);
    }

    public static CoreTranscriptReply ToWire(TranscriptResult result, bool running) => result switch
    {
        TranscriptResult.Inventory inv => new CoreTranscriptReply(
            "inventory", Eof: true, Running: running, Instances: inv.Instances),
        TranscriptResult.Range range => new CoreTranscriptReply(
            "range", range.Text, range.NextOffset, range.Eof, running),
        TranscriptResult.Unavailable u => new CoreTranscriptReply(
            "unavailable", Running: running, Reason: u.Reason.ToString(), Detail: u.Detail),
        _ => new CoreTranscriptReply("unavailable", Reason: nameof(TranscriptUnavailable.Timeout), Detail: "unexpected reply"),
    };

    public static int StatusCode(TranscriptUnavailable reason) => reason switch
    {
        TranscriptUnavailable.NoSuchSession => StatusCodes.Status404NotFound,
        TranscriptUnavailable.NotTerminal => StatusCodes.Status409Conflict,
        TranscriptUnavailable.Busy => StatusCodes.Status409Conflict,
        TranscriptUnavailable.MachineRefused => StatusCodes.Status404NotFound,
        TranscriptUnavailable.NotServable => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status503ServiceUnavailable,
    };

    private static TranscriptResult Map(CoreTranscriptReply? body, Guid machine)
    {
        if (body is null)
        {
            return new TranscriptResult.Unavailable(
                TranscriptUnavailable.Timeout,
                "The control plane did not answer the transcript read.");
        }

        if (body.Kind == "inventory")
            return new TranscriptResult.Inventory(machine, body.Instances ?? []);
        if (body.Kind == "range")
            return new TranscriptResult.Range(machine, body.Text ?? "", body.NextOffset, body.Eof);
        var reason = Enum.TryParse<TranscriptUnavailable>(body.Reason, out var parsed)
            ? parsed
            : TranscriptUnavailable.Timeout;
        return new TranscriptResult.Unavailable(reason, body.Detail ?? "Transcript unavailable.");
    }
}

/// <summary>One transcript read as Core returns it. <see cref="Text"/> is verbatim and must not be logged.</summary>
public sealed record CoreTranscriptReply(
    string Kind,
    string? Text = "",
    long NextOffset = 0,
    bool Eof = false,
    bool Running = false,
    string? Reason = null,
    string? Detail = null,
    IReadOnlyList<TranscriptInstance>? Instances = null);
