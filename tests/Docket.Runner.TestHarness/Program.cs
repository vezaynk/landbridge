using System.Diagnostics;

namespace Docket.Runner.TestHarness;

/// <summary>
/// A minimal, cross-platform stand-in for a real harness that the runner tests
/// spawn as a real process — no shell, argv only, matching docketd's own
/// constraint (§10). It writes markers into its working directory (which the
/// supervisor sets to <c>{work_root}/{task_id}</c>), so a test can observe env
/// injection, working directory, stop delivery, and process-tree membership
/// without depending on any installed harness.
///
/// Modes (argv[0]):
///   run          — write the started marker, then sleep until killed.
///   spawn-child  — like run, but first fork a grandchild sleeper and record
///                  its pid, so tree-kill can be verified.
///   stdin-stop   — like run, but read stdin; on a "stop" line, persist and exit
///                  (the graceful message-delivered wind-down of §10/§11).
///   child        — a bare sleeper (the grandchild).
/// </summary>
public static class Program
{
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(120);

    public static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "run";
        var cwd = Directory.GetCurrentDirectory();

        switch (mode)
        {
            case "child":
                await Task.Delay(MaxLifetime);
                return 0;

            case "spawn-child":
                await WriteStartedAsync(cwd);
                using (var grandchild = SpawnChild())
                {
                    await File.WriteAllTextAsync(Path.Combine(cwd, "child.pid"), grandchild.Id.ToString());
                    await Task.Delay(MaxLifetime);
                }
                return 0;

            case "stdin-stop":
                await WriteStartedAsync(cwd);
                string? line;
                while ((line = await Console.In.ReadLineAsync()) is not null)
                {
                    if (line.Contains("stop", StringComparison.OrdinalIgnoreCase))
                    {
                        await File.WriteAllTextAsync(Path.Combine(cwd, "stopped"), line);
                        return 0;
                    }
                }
                return 0;

            default: // "run"
                await WriteStartedAsync(cwd);
                await Task.Delay(MaxLifetime);
                return 0;
        }
    }

    private static async Task WriteStartedAsync(string cwd)
    {
        var taskId = Environment.GetEnvironmentVariable("DOCKET_TASK_ID") ?? "none";
        var machineId = Environment.GetEnvironmentVariable("DOCKET_MACHINE_ID") ?? "none";
        await File.WriteAllTextAsync(Path.Combine(cwd, "started"), $"{taskId}\n{machineId}");
    }

    private static Process SpawnChild()
    {
        var self = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve own binary path");
        var psi = new ProcessStartInfo(self) { UseShellExecute = false };
        psi.ArgumentList.Add("child");
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to spawn grandchild");
    }
}
