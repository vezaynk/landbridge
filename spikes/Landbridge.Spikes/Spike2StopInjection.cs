using System.Text.Json;

namespace Landbridge.Spikes;

/// <summary>
/// S2 — stop delivered as an injected turn (spec §10, §17.0b). Proves that
/// with --input-format stream-json, a queued user message carrying a stop
/// disposition reaches a busy agent as its own turn, producing a graceful
/// wind-down (persist + exit) instead of a signal-style abort.
/// </summary>
internal static class Spike2StopInjection
{
    private const int Steps = 6;

    private static readonly string TaskPrompt =
        $"You are a landbridge worker. Work through this slowly: create files step1.txt through step{Steps}.txt, " +
        "one per turn, running 'sleep 3' between each. Do not create them all at once.";

    private const string StopPrompt =
        "STOP (ttl: 60s, disposition: preserve). Wind down now: stop starting new steps, " +
        "write a one-line summary of exactly which steps you completed to progress.txt, and end the session.";

    public static async Task<int> Run()
    {
        var outDir = SpikeEnv.OutDir("s2");
        var workDir = Directory.CreateDirectory(Path.Combine(outDir, "work")).FullName;

        Console.WriteLine("== launching worker on a deliberately slow multi-step task, injecting stop after 20s");
        var result = await ClaudeCli.Run(
            ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
             "--model", SpikeEnv.Model, "--allowedTools", "Bash,Write"],
            workDir, TimeSpan.FromMinutes(3),
            writeStdIn: async stdin =>
            {
                await stdin.WriteLineAsync(UserMessage(TaskPrompt));
                await stdin.FlushAsync();
                await Task.Delay(TimeSpan.FromSeconds(20));
                Console.WriteLine("== injecting stop message");
                await stdin.WriteLineAsync(UserMessage(StopPrompt));
                await stdin.FlushAsync();
            });

        await File.WriteAllTextAsync(Path.Combine(outDir, "events.jsonl"), result.StdOut);
        await File.WriteAllTextAsync(Path.Combine(outDir, "stderr.log"), result.StdErr);

        Console.WriteLine("== results");
        var stepsDone = Directory.GetFiles(workDir, "step*.txt").Length;
        Console.WriteLine($"   steps completed before stop: {stepsDone} / {Steps}");
        var pass = true;

        var progressPath = Path.Combine(workDir, "progress.txt");
        if (File.Exists(progressPath))
        {
            SpikeEnv.Report(true, $"wind-down persisted progress.txt: {(await File.ReadAllTextAsync(progressPath)).Trim()}");
        }
        else
        {
            SpikeEnv.Report(false, "no progress.txt — the stop did not produce a wind-down turn");
            pass = false;
        }

        if (result.TimedOut)
        {
            SpikeEnv.Report(false, "worker did not exit before the hard timeout — stop did not end the session");
            pass = false;
        }

        if (stepsDone < Steps)
            SpikeEnv.Report(true, "stop interrupted the task before completion (disposition honored, not ignored)");
        else
            Console.WriteLine("   AMBIGUOUS: all steps finished — task too fast; increase step count or sleep");

        Console.WriteLine($"   event stream: {outDir}/events.jsonl (verify the stop appears as a user turn, not a truncation)");
        return pass ? 0 : 1;
    }

    private static string UserMessage(string text) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = new[] { new { type = "text", text } } },
    });
}
