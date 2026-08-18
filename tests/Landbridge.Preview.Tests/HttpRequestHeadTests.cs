using System.Text;
using Landbridge.Preview;

namespace Landbridge.Preview.Tests;

/// <summary>
/// The routing-only request-head reader (spec §8.4): it extracts just the Host, the
/// operator session, and whether it is a WS upgrade, and keeps the raw head to
/// replay — it never rewrites. Pure parsing; no sockets.
/// </summary>
public sealed class HttpRequestHeadTests
{
    [Theory]
    [InlineData("abc.preview.localhost", "abc")]
    [InlineData("ABC.preview.localhost", "abc")]          // DNS is case-insensitive
    [InlineData("abc.preview.localhost:8443", "abc")]     // port stripped
    [InlineData("preview.localhost", null)]               // apex has no label
    [InlineData("a.b.preview.localhost", null)]           // structure is never encoded — one label only
    [InlineData("abc.evil.com", null)]                    // wrong base domain
    [InlineData("", null)]
    public void LabelUnder_extracts_only_a_single_leftmost_label(string host, string? expected)
    {
        var head = new HttpRequestHead
        {
            RawHead = [],
            Extra = [],
            Host = host.Length == 0 ? null : host,
        };
        Assert.Equal(expected, head.LabelUnder("preview.localhost"));
    }

    [Fact]
    public async Task ReadAsync_parses_host_cookie_session_and_keeps_the_raw_head()
    {
        var request =
            "GET /deep/path?x=1 HTTP/1.1\r\n" +
            "Host: abc.preview.localhost\r\n" +
            "Cookie: other=1; landbridge_session=tok-123; last=2\r\n" +
            "Accept: */*\r\n" +
            "\r\n";
        var extra = "not-part-of-head"u8.ToArray();
        var bytes = Concat(Encoding.Latin1.GetBytes(request), extra);

        var head = await HttpRequestHead.ReadAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.NotNull(head);
        Assert.Equal("abc.preview.localhost", head!.Host);
        Assert.Equal("tok-123", head.OperatorSession);
        Assert.False(head.IsWebSocketUpgrade);
        // The head is kept verbatim (ends at the blank line); trailing bytes are Extra.
        Assert.Equal(Encoding.Latin1.GetBytes(request), head.RawHead);
        Assert.Equal(extra, head.Extra);
    }

    [Fact]
    public async Task ReadAsync_prefers_bearer_over_cookie_and_flags_ws_upgrade()
    {
        var request =
            "GET /ws HTTP/1.1\r\n" +
            "Host: abc.preview.localhost\r\n" +
            "Authorization: Bearer btok\r\n" +
            "Cookie: landbridge_session=ctok\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "\r\n";
        var head = await HttpRequestHead.ReadAsync(
            new MemoryStream(Encoding.Latin1.GetBytes(request)), CancellationToken.None);

        Assert.NotNull(head);
        Assert.Equal("btok", head!.OperatorSession); // bearer wins (mirrors DashboardAuth)
        Assert.True(head.IsWebSocketUpgrade);
    }

    [Fact]
    public async Task ReadAsync_returns_null_when_the_connection_closes_before_a_full_head()
    {
        var partial = "GET / HTTP/1.1\r\nHost: abc.preview.localhost\r\n"; // no terminating blank line
        var head = await HttpRequestHead.ReadAsync(
            new MemoryStream(Encoding.Latin1.GetBytes(partial)), CancellationToken.None);
        Assert.Null(head);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
