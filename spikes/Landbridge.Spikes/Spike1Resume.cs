using System.Text.Json;

namespace Landbridge.Spikes;

/// <summary>
/// S1 — resume-after-park (spec §11, §17.0a). Proves headless resume works
/// with a new prompt, retains session context, and is scoped to the directory
/// that created the session.
/// </summary>
internal static class Spike1Resume
{
    private const string Codeword = "PELICAN-7";

    public static async Task<int> Run()
    {
        var outDir = SpikeEnv.OutDir("s1");
        var dirA = Directory.CreateDirectory(Path.Combine(outDir, "dir-a")).FullName;
        var dirB = Directory.CreateDirectory(Path.Combine(outDir, "dir-b")).FullName;
        var pass = true;

        Console.WriteLine("== phase 1: create a session in dir-a with a memorable fact");
        var create = await ClaudeCli.Run(
            ["-p", $"Remember this codeword and confirm you have it: {Codeword}. Reply with one short sentence.",
             "--model", SpikeEnv.Model, "--output-format", "json"],
            dirA, TimeSpan.FromMinutes(3));
        await File.WriteAllTextAsync(Path.Combine(outDir, "create.json"), create.StdOut);
        if (create.ExitCode != 0)
        {
            SpikeEnv.Report(false, $"session creation failed (exit {create.ExitCode}): {create.StdErr}");
            return 1;
        }

        using var created = JsonDocument.Parse(create.StdOut);
        var sessionId = created.RootElement.GetProperty("session_id").GetString()!;
        Console.WriteLine($"   session: {sessionId}");

        Console.WriteLine("== phase 2: resume from dir-a (the park record's directory)");
        var resumeA = await ClaudeCli.Run(
            ["-p", "--resume", sessionId, "What was the codeword? Reply with the codeword only.",
             "--model", SpikeEnv.Model, "--output-format", "json"],
            dirA, TimeSpan.FromMinutes(3));
        await File.WriteAllTextAsync(Path.Combine(outDir, "resume-a.json"), resumeA.StdOut);

        var recalled = false;
        if (resumeA.ExitCode == 0)
        {
            using var resumed = JsonDocument.Parse(resumeA.StdOut);
            recalled = (resumed.RootElement.GetProperty("result").GetString() ?? "").Contains(Codeword);
        }
        SpikeEnv.Report(recalled, recalled
            ? $"resumed session recalled '{Codeword}'"
            : $"resume in the creating directory did not recall the codeword (exit {resumeA.ExitCode})");
        pass &= recalled;

        Console.WriteLine("== phase 3: attempt resume from dir-b (a different machine, effectively)");
        var resumeB = await ClaudeCli.Run(
            ["-p", "--resume", sessionId, "What was the codeword?",
             "--model", SpikeEnv.Model, "--output-format", "json"],
            dirB, TimeSpan.FromMinutes(3));
        await File.WriteAllTextAsync(Path.Combine(outDir, "resume-b.json"), resumeB.StdOut + resumeB.StdErr);

        if (resumeB.ExitCode != 0)
        {
            SpikeEnv.Report(true, "expected failure — session lookup is directory-scoped");
        }
        else
        {
            SpikeEnv.Report(false, "resume succeeded outside the creating directory — spec §11's affinity assumption is WRONG; re-check");
            pass = false;
        }

        Console.WriteLine($"== done. Evidence in {outDir} — record versions and flags in FINDINGS.md");
        return pass ? 0 : 1;
    }
}
