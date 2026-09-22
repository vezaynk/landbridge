using Landbridge.Mcp;
using Microsoft.AspNetCore.Http;

namespace Landbridge.Mcp.Tests;

public sealed class PreferHeaderTests
{
    [Theory]
    [InlineData("respond-async", true)]
    [InlineData("respond-async, wait=10", true)]
    [InlineData("wait=10, respond-async", true)]
    [InlineData("Respond-Async", true)]
    [InlineData("return=representation", false)]
    [InlineData("", false)]
    public void WantsRespondAsync_reads_rfc7240_tokens(string header, bool expected)
    {
        var http = new DefaultHttpContext();
        if (header.Length > 0)
            http.Request.Headers["Prefer"] = header;
        Assert.Equal(expected, PreferHeader.WantsRespondAsync(http.Request));
    }
}
