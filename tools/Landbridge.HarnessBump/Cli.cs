using System.Diagnostics;
using System.Text.Json;

namespace Docket.HarnessBump;

public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    public string Describe(string program, IReadOnlyList<string> arguments) =>
        $"`{program} {string.Join(' ', arguments)}` exited {ExitCode}" +
        (StdErr.Length > 0 ? $": {StdErr.Trim()}" : "");
}

/// <summary>
/// Spawns <c>gh</c> and <c>git</c> as argv — no shell — matching the repo-wide constraint
/// that all tooling is C# and every external process is an argv spawn (spec §10).
/// </summary>
/// <remarks>
/// Not merely a style rule here: every value this tool interpolates into a command line is
/// attacker-adjacent in the sense that matters (a version string comes off a third-party
/// registry, a PR title carries it, a branch name is built from it). With argv there is no
/// quoting layer to get wrong.
/// </remarks>
public sealed class Cli(Action<string> log)
{
    public async Task<CliResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? stdIn = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdIn is not null,
            UseShellExecute = false,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start `{program}` — is it on PATH?");

        var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (stdIn is not null)
        {
            await process.StandardInput.WriteAsync(stdIn);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync(cancellationToken);

        return new CliResult(process.ExitCode, await stdOut, await stdErr);
    }

    /// <summary>Runs a command and throws unless it exits 0.</summary>
    public async Task<string> CheckedAsync(
        string program,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        log($"$ {program} {string.Join(' ', arguments)}");
        var result = await RunAsync(program, arguments, workingDirectory, stdIn: null, cancellationToken);
        if (!result.Ok)
            throw new InvalidOperationException(result.Describe(program, arguments));
        return result.StdOut;
    }

    /// <summary>Runs a <c>gh ... --json ...</c> command and deserializes the result.</summary>
    public async Task<T> GhJsonAsync<T>(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var stdout = await CheckedAsync("gh", arguments, workingDirectory, cancellationToken);
        return JsonSerializer.Deserialize<T>(stdout, JsonOptions)
            ?? throw new InvalidOperationException($"gh returned no JSON for `{string.Join(' ', arguments)}`.");
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
