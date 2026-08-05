using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Docket.Chaos.Tests;

/// <summary>
/// A real OS process this suite owns, with both output streams drained into a
/// bounded in-memory ring so (a) a full pipe can never block the child — the trap
/// <c>DocketdStrayReapEndToEndTests</c> guards against by draining stderr — and
/// (b) a failing scenario can dump a tail of what the process actually said.
///
/// <para>Every wait here takes a hard deadline and returns false rather than
/// throwing or hanging: a chaos suite that wedges CI for the full runner timeout
/// is worse than no suite, so the callers turn a false into an assertion failure
/// carrying <see cref="Tail"/>.</para>
/// </summary>
internal sealed class ChaosProcess : IDisposable
{
    /// <summary>
    /// How much output to retain. Deep enough that a line a scenario is waiting for
    /// cannot be evicted by a chatty host between two polls, while
    /// <see cref="Tail()"/> still prints only the most recent <see cref="DumpLines"/>
    /// so a failure dump stays readable.
    /// </summary>
    private const int RetainedLines = 400;

    /// <summary>How many of the retained lines a failure dump prints.</summary>
    private const int DumpLines = 60;

    private readonly ConcurrentQueue<string> _lines = new();
    private readonly List<Task> _drains = new();

    private ChaosProcess(string name, Process process)
    {
        Name = name;
        Process = process;
    }

    public string Name { get; }
    public Process Process { get; }
    public int Id => Process.Id;
    public bool Alive => !Process.HasExited;

    /// <summary>
    /// Starts <paramref name="exePath"/> with argv only — no shell, ever (repo
    /// convention, §10). <paramref name="environment"/> is applied on top of the
    /// inherited environment; note that secrets (machine tokens) travel there and
    /// never in <paramref name="args"/> (§13).
    /// </summary>
    public static ChaosProcess Start(
        string name,
        string exePath,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory = null,
        bool redirectStdin = false)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in environment)
            psi.Environment[key] = value;

        var handle = new ChaosProcess(name, Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {name} ({exePath})"));
        handle._drains.Add(handle.DrainAsync(handle.Process.StandardOutput, "out"));
        handle._drains.Add(handle.DrainAsync(handle.Process.StandardError, "err"));
        return handle;
    }

    private async Task DrainAsync(StreamReader reader, string stream)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                _lines.Enqueue($"[{stream}] {line}");
                while (_lines.Count > RetainedLines)
                    _lines.TryDequeue(out _);
            }
        }
        catch
        {
            // the process died mid-read, or the pipe was torn down at teardown
        }
    }

    /// <summary>
    /// Waits for a line matching <paramref name="match"/> to appear on either
    /// stream, up to <paramref name="timeout"/>. Polls the drained ring rather than
    /// reading the stream directly, so it composes with the tail buffer and cannot
    /// steal a line the diagnostics dump needs. Returns the line, or null at the
    /// deadline / if the process exits first.
    /// </summary>
    public async Task<string?> WaitForLineAsync(Func<string, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var line in _lines)
                if (match(line))
                    return line;
            if (Process.HasExited)
            {
                // One last look: the drain may still have been flushing at exit.
                await Task.WhenAny(Task.WhenAll(_drains), Task.Delay(TimeSpan.FromSeconds(2)));
                foreach (var line in _lines)
                    if (match(line))
                        return line;
                return null;
            }
            await Task.Delay(50);
        }
        return null;
    }

    /// <summary>
    /// SIGKILL — the uncatchable death §17.8 asks for. <see cref="Process.Kill()"/>
    /// is SIGKILL on Unix, so no handler runs, nothing is flushed, and no child is
    /// cleaned up: exactly the crash the restart sweep has to survive. Deliberately
    /// NOT <c>entireProcessTree</c>, because leaving the children orphaned is the
    /// point of the scenario.
    /// </summary>
    public void Sigkill()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: false);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // already gone
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await Process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The retained tail of both streams, for a failing scenario's dump.</summary>
    public string Tail()
    {
        var all = _lines.ToArray();
        var lines = all.Length > DumpLines ? all[^DumpLines..] : all;
        var status = Process.HasExited ? $"exited code={SafeExitCode()}" : "running";
        var elided = all.Length > lines.Length ? $" (of {all.Length} retained)" : "";
        return $"── {Name} (pid={SafePid()}, {status}) last {lines.Length} line(s){elided}:\n" +
               (lines.Length == 0 ? "    (no output)\n" : string.Concat(lines.Select(l => "    " + l + "\n")));
    }

    private string SafePid()
    {
        try { return Process.Id.ToString(); } catch (InvalidOperationException) { return "?"; }
    }

    private string SafeExitCode()
    {
        try { return Process.ExitCode.ToString(); } catch (InvalidOperationException) { return "?"; }
    }

    /// <summary>Best-effort teardown: kill the whole tree, then release the handle.</summary>
    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        try { Process.WaitForExit(5_000); } catch { /* best effort */ }
        Process.Dispose();
    }

    /// <summary>Whether a pid is still a live process — for asserting a stray was reaped.</summary>
    public static bool PidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
