using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Docket.AcpBridge;

/// <summary>
/// Hosts <c>/acp</c> as a WebSocket and, per connection, spawns the agent argv
/// and pumps NDJSON ↔ text frames until either side ends.
/// </summary>
internal static class ListenCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var bind = "127.0.0.1:0";
        string[]? agent = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                agent = args[(i + 1)..].ToArray();
                break;
            }

            if (a == "--bind")
            {
                if (i + 1 >= args.Length)
                    return Program.Fail("--bind needs HOST:PORT");
                bind = args[++i];
                continue;
            }

            if (a.StartsWith('-'))
                return Program.Fail($"unknown listen flag '{a}'");

            agent = args[i..].ToArray();
            break;
        }

        if (agent is not { Length: > 0 })
            return Program.Fail("listen needs an agent argv after --");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.SuppressStatusMessages(true);
        builder.WebHost.UseUrls("http://" + bind);

        var app = builder.Build();
        app.UseWebSockets();
        var agentArgv = agent;
        var busy = new SemaphoreSlim(1, 1);
        app.Map("/acp", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!await busy.WaitAsync(0).ConfigureAwait(false))
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            try
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await AgentSession.RunAsync(ws, agentArgv, ctx.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                busy.Release();
            }
        });

        await app.StartAsync().ConfigureAwait(false);
        var http = app.Urls.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel bound no address");
        var wsUrl = http.Replace("http://", "ws://", StringComparison.Ordinal) + "/acp";
        await Console.Out.WriteLineAsync("listening " + wsUrl).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);

        await app.WaitForShutdownAsync().ConfigureAwait(false);
        return 0;
    }
}
