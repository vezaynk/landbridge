namespace Docket.Spikes;

/// <summary>
/// Step-0 feasibility spikes (spec §17.0). Operator-run, token-consuming;
/// never CI. Everything spawns `claude` as argv — no shell anywhere, matching
/// docketd's own constraint (§10).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args) => args switch
    {
        ["s1"] => await Spike1Resume.Run(),
        ["s2"] => await Spike2StopInjection.Run(),
        ["s3"] => await Spike3HookEvents.Run(),
        ["hook-relay", var url] => await HookRelay.Run(url),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine("usage: docket-spikes <s1|s2|s3>");
        Console.Error.WriteLine("  s1  resume-after-park: directory-scoped resume with context retention");
        Console.Error.WriteLine("  s2  stop as an injected stream-json turn: wind-down, not abort");
        Console.Error.WriteLine("  s3  per-tool-call hook events carrying DOCKET_TASK_ID");
        Console.Error.WriteLine("env: DOCKET_SPIKE_MODEL (default: haiku)");
        return 2;
    }
}

internal static class SpikeEnv
{
    public static string Model =>
        Environment.GetEnvironmentVariable("DOCKET_SPIKE_MODEL") ?? "haiku";

    /// <summary>spikes/out/&lt;name&gt;, located relative to this project.</summary>
    public static string OutDir(string name)
    {
        // bin/Debug/net10.0 → Docket.Spikes → spikes
        var spikesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var dir = Path.Combine(spikesDir, "out", name);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Report(bool pass, string message) =>
        Console.WriteLine($"   {(pass ? "PASS" : "FAIL")}: {message}");
}
