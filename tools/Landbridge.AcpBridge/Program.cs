namespace Landbridge.AcpBridge;

/// <summary>
/// Stdio ↔ WebSocket pipe for ACP. <c>listen</c> accepts one WebSocket at
/// <c>/acp</c> and attaches it to a spawned agent's stdin/stdout.
/// <c>connect</c> is the other half: a profile <c>spawn</c> entry that
/// forwards landbridged's ACP conversation to that socket.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine(
                """
                landbridge-acp-bridge listen [--bind 127.0.0.1:0] -- <agent argv>
                landbridge-acp-bridge connect <ws-url>

                listen prints one 'listening <url>' line on stdout, then only
                stderr. connect uses stdin/stdout as the ACP NDJSON channel.
                """);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "listen" => await ListenCommand.RunAsync(args[1..]),
                "connect" => await ConnectCommand.RunAsync(args[1..]),
                _ => Fail($"unknown command '{args[0]}' (want listen or connect)"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine("landbridge-acp-bridge: " + ex.Message);
            return 1;
        }
    }

    internal static int Fail(string message)
    {
        Console.Error.WriteLine("landbridge-acp-bridge: " + message);
        return 2;
    }
}
