using System.Text;

namespace Docket.Preview;

/// <summary>
/// Minimal HTTP/1.1 error responses the frontend writes directly to the browser
/// when a connection is refused <em>before</em> the tunnel produced any byte (spec
/// §8.4: failure modes return clean statuses, not hangs). Once the upstream
/// response is streaming, errors are just the connection closing — these cover the
/// pre-splice refusals only (bad Host, unknown/expired label, gated-without-session,
/// no upstream). Every response is <c>Connection: close</c>: the frontend serves
/// exactly one routing decision per connection.
/// </summary>
internal static class PreviewHttpResponses
{
    public static Task BadRequestAsync(Stream s, string detail, CancellationToken ct) =>
        WriteAsync(s, 400, "Bad Request", detail, ct);

    public static Task UnauthorizedAsync(Stream s, CancellationToken ct) =>
        WriteAsync(s, 401, "Unauthorized",
            "This preview requires a valid Docket operator session.", ct);

    public static Task NotFoundAsync(Stream s, CancellationToken ct) =>
        WriteAsync(s, 404, "Not Found", "No such preview.", ct);

    public static Task GoneAsync(Stream s, CancellationToken ct) =>
        WriteAsync(s, 410, "Gone", "This preview has expired.", ct);

    public static Task BadGatewayAsync(Stream s, CancellationToken ct) =>
        WriteAsync(s, 502, "Bad Gateway",
            "The previewed service could not be reached.", ct);

    public static Task ServiceUnavailableAsync(Stream s, CancellationToken ct) =>
        WriteAsync(s, 503, "Service Unavailable",
            "The previewed service is not currently available.", ct);

    public static Task GatewayTimeoutAsync(Stream s, CancellationToken ct) =>
        WriteAsync(s, 504, "Gateway Timeout",
            "The previewed service did not respond in time.", ct);

    private static async Task WriteAsync(Stream s, int code, string reason, string body, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(body + "\n");
        var head =
            $"HTTP/1.1 {code} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        try
        {
            await s.WriteAsync(Encoding.Latin1.GetBytes(head), ct);
            await s.WriteAsync(payload, ct);
            await s.FlushAsync(ct);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The browser may have already gone; a refusal is best-effort.
        }
    }
}
