using System.Text.Json;
using System.Text.Json.Nodes;
using Landbridge.ControlPlane;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Per-box landbridged config for the Aspire loop. Spawn is the real ACP
/// harness; provider keys stay out of this file and ride landbridged's env.
/// </summary>
internal static class DevBoxConfig
{
    // Same id as tests/Landbridge.MultiMachine.Tests — the paid e2e store.
    public const string MultiMachineSecretsId = "a7e4c8b2-1f93-4d6a-9e20-6c8f1b0d4e7a";

    public sealed record ProviderKeys(string? Anthropic, string? Codex, string? Xai)
    {
        public string? For(string harness) => harness switch
        {
            "claude" => Anthropic,
            "codex" => Codex,
            "grok" => Xai,
            _ => null,
        };
    }

    public static string CanonicalKeyName(string harness) => harness switch
    {
        "claude" => "ANTHROPIC_API_KEY",
        "codex" => "CODEX_API_KEY",
        "grok" => "XAI_API_KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "unknown Aspire-seeded harness"),
    };

    public static string? FirstNonEmpty(IConfiguration config, params string[] names)
    {
        foreach (var name in names)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } env
                && !string.IsNullOrWhiteSpace(env))
                return env;
        }

        foreach (var name in names)
        {
            if (config[name] is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    public static string Write(string runDir, string workRoot, string harness, string specificProfile)
    {
        var recipe = ForHarness(harness);
        var doc = new JsonObject
        {
            ["machine"] = new JsonObject
            {
                ["work_root"] = workRoot,
                ["heartbeat_seconds"] = 2,
            },
            ["profiles"] = new JsonArray(
                Profile(specificProfile, recipe),
                Profile(DevSeedNaming.Group, recipe)),
        };

        var outPath = Path.Combine(runDir, $"landbridged.{specificProfile}.json");
        File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return outPath;
    }

    private sealed record Recipe(
        string[] Spawn,
        string Prompt,
        string FollowUp,
        string? AuthMethod,
        JsonObject? Env,
        JsonObject? Telemetry);

    private static Recipe ForHarness(string harness) => harness switch
    {
        "claude" => new(
            [ResolveBin("claude-agent-acp")],
            Prompt("mcp__landbridge__get_session", "mcp__landbridge__report_result", "mcp__landbridge__request_input"),
            FollowUp("mcp__landbridge__get_session"),
            AuthMethod: null,
            Env: null,
            Telemetry: new JsonObject
            {
                ["otel"] = true,
                ["env"] = new JsonObject
                {
                    ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1",
                    ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
                    ["OTEL_METRIC_EXPORT_INTERVAL"] = "5000",
                },
            }),
        "codex" => new(
            [ResolveBin("codex-acp")],
            Prompt("mcp__landbridge__get_session", "mcp__landbridge__report_result", "mcp__landbridge__request_input"),
            FollowUp("mcp__landbridge__get_session"),
            AuthMethod: "api-key",
            Env: null,
            Telemetry: null),
        "grok" => new(
            [ResolveBin("grok"), "agent", "stdio"],
            Prompt("landbridge__get_session", "landbridge__report_result", "landbridge__request_input"),
            FollowUp("landbridge__get_session"),
            AuthMethod: null,
            Env: new JsonObject { ["GROK_FOLDER_TRUST"] = "0" },
            Telemetry: null),
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "unknown Aspire-seeded harness"),
    };

    private static JsonObject Profile(string name, Recipe recipe)
    {
        var spawn = new JsonArray();
        foreach (var arg in recipe.Spawn)
            spawn.Add(arg);

        var profile = new JsonObject
        {
            ["name"] = name,
            ["spawn"] = spawn,
            ["prompt"] = recipe.Prompt,
            ["follow_up"] = recipe.FollowUp,
            ["stop"] = new JsonObject { ["wind_down_seconds"] = 30 },
            ["logs"] = new JsonObject { ["capture"] = true },
            ["processes"] = new JsonObject { ["agent_initiated"] = true },
        };
        if (recipe.AuthMethod is not null)
            profile["auth_method"] = recipe.AuthMethod;
        if (recipe.Env is not null)
            profile["env"] = recipe.Env.DeepClone();
        if (recipe.Telemetry is not null)
            profile["telemetry"] = recipe.Telemetry.DeepClone();
        profile["files"] = new JsonArray(new JsonObject
        {
            ["path"] = "LANDING.md",
            ["contents"] = LandingMarkdown(recipe.Prompt.Contains("mcp__landbridge__", StringComparison.Ordinal)
                ? "mcp__landbridge__"
                : "landbridge__"),
        });
        return profile;
    }

    private static string Prompt(string getSession, string reportResult, string requestInput) =>
        $"You are a Landbridge worker on a live session. Read LANDING.md in this directory " +
        $"first — do not search $HOME or ~/.claude for a landbridge skill. Then call the " +
        $"{getSession} MCP tool to read your assignment (namespace, description, workspace, " +
        "attempt). Do the work in this session's directory; you are not the only agent on " +
        "the machine. You must not end a turn until you have called " +
        $"{reportResult} or {requestInput}. When you think you are done, call {reportResult} " +
        "with a reference to where the work lives (a branch/commit/URL) — not the work " +
        "itself — and stay up; the Lead may reply. If you are blocked or a decision is " +
        $"above your scope, call {requestInput} instead of guessing. You do not complete " +
        "the session yourself.";

    private static string LandingMarkdown(string toolPrefix) =>
        $"""
         # Landbridge worker contract

         You were dispatched. This file is the contract. The skill is not in ~/.claude.

         1. Call `{toolPrefix}get_session` first. Stay up after you report.
         2. Work only in this directory. Isolate: worktree, random port, unique names.
         3. Call `{toolPrefix}report_result` with a reference (branch/commit/URL), or `{toolPrefix}request_input` if blocked.
         4. Do not write `$HOME`, `~/.ssh`, `~/.claude`, or change global git/npm config.
         5. Do not search the operator's dotfiles for a landbridge skill. This file is it.
         6. Long work: `{toolPrefix}start_process` when the tool exists.

         Landbridge tools are MCP tools, not a `landbridge` binary.
         """;

    private static string FollowUp(string getSession) =>
        $"There is new input on your assignment. Call {getSession} to read it, then continue.";

    // Prefer an absolute path so landbridged does not depend on Aspire's PATH
    // matching the operator's shell. Fall back to the bare name if nothing is
    // found — landbridged will then search its own PATH and fail loudly.
    internal static string ResolveBin(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var fallback in new[]
                 {
                     Path.Combine("/usr/local/bin", exe),
                     Path.Combine("/opt/homebrew/bin", exe),
                     Path.Combine(home, ".grok", "bin", exe),
                     Path.Combine(home, ".local", "bin", exe),
                 })
        {
            if (File.Exists(fallback)) return fallback;
        }

        Console.Error.WriteLine(
            $"landbridge-apphost: {name} not found on PATH; spawn uses the bare name.");
        return name;
    }
}
