using System.Diagnostics;

namespace Landbridge.Spikes;

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Spawns `claude` with argv via execve semantics — no shell — mirroring
/// landbridged's runner constraint (spec §10).
/// </summary>
internal static class ClaudeCli
{
    public static async Task<CliResult> Run(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        Func<StreamWriter, Task>? writeStdIn = null)
    {
        var psi = new ProcessStartInfo("claude")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = writeStdIn is not null,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start claude — is it on PATH and logged in?");

        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();

        if (writeStdIn is not null)
        {
            await writeStdIn(process.StandardInput);
            process.StandardInput.Close();
        }

        using var cts = new CancellationTokenSource(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        return new CliResult(process.ExitCode, await stdOut, await stdErr, timedOut);
    }
}
