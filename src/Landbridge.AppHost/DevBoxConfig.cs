using System.Text.Json;
using System.Text.Json.Nodes;
using Landbridge.ControlPlane;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Per-box landbridged config for the Aspire loop. Spawn is the real ACP
/// harness on PATH inside the Linux container; provider keys stay out of this
/// file and ride landbridged's env.
/// </summary>
internal static class DevBoxConfig
{
    // Same id as tests/Landbridge.MultiMachine.Tests — the paid e2e store.
    public const string MultiMachineSecretsId = "a7e4c8b2-1f93-4d6a-9e20-6c8f1b0d4e7a";

    /// <summary>Container-side work_root. Bind-mounted from the host scratch dir.</summary>
    public const string ContainerWorkRoot = "/work";

    /// <summary>Container-side state dir. Bind-mounted from the host scratch dir.</summary>
    public const string ContainerStateDir = "/state";

    /// <summary>Container-side path of the generated runner config.</summary>
    public const string ContainerConfigPath = "/config/landbridged.json";

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

    /// <summary>
    /// Same default as the paid Codex e2e. The adapter's advertised default
    /// (<c>gpt-5.6-sol</c>) is not on this project's API-key catalog; pinning
    /// via <c>CODEX_HOME/config.toml</c> is what actually changes the model,
    /// because ACP <c>config_options</c> only accepts advertised slugs.
    /// </summary>
    public static string CodexModel(IConfiguration config) =>
        FirstNonEmpty(config, "LANDBRIDGE_CODEX_MODEL") ?? "gpt-5.3-codex";

    /// <summary>Container-side <c>CODEX_HOME</c>. Lives under the bind-mounted state dir.</summary>
    public const string ContainerCodexHome = "/state/codex-home";

    public static void WriteCodexHome(string boxState, string model)
    {
        var dir = Path.Combine(boxState, "codex-home");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.toml"), $"model = \"{model}\"\n");
    }

    /// <summary>
    /// Same default as the paid Grok e2e. <c>GROK_DEFAULT_MODEL</c> is the pin
    /// grok actually reads: <c>--model</c> on argv is ignored by
    /// <c>agent stdio</c> on 1.0.3 (#222 still sat on
    /// <c>grok-4.20-0309-non-reasoning</c>). ACP <c>config_options</c> is
    /// skipped unless advertised.
    /// </summary>
    public static string GrokModel(IConfiguration? config = null) =>
        (config is null
            ? FirstNonEmptyEnv("LANDBRIDGE_GROK_MODEL")
            : FirstNonEmpty(config, "LANDBRIDGE_GROK_MODEL")) ?? "grok-4.6";

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

    private static string? FirstNonEmptyEnv(params string[] names)
    {
        foreach (var name in names)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } env
                && !string.IsNullOrWhiteSpace(env))
                return env;
        }

        return null;
    }

    public static string Write(string runDir, string harness, string specificProfile)
    {
        var recipe = ForHarness(harness);
        var doc = new JsonObject
        {
            ["machine"] = new JsonObject
            {
                ["work_root"] = ContainerWorkRoot,
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
        JsonObject? Telemetry,
        JsonObject? ConfigOptions = null);

    private static Recipe ForHarness(string harness) => harness switch
    {
        "claude" => new(
            ["claude-agent-acp"],
            Prompt("mcp__landbridge__get_inbox", "mcp__landbridge__report_result", "mcp__landbridge__request_input"),
            FollowUp("mcp__landbridge__get_inbox"),
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
            ["codex-acp"],
            Prompt("mcp__landbridge__get_inbox", "mcp__landbridge__report_result", "mcp__landbridge__request_input"),
            FollowUp("mcp__landbridge__get_inbox"),
            AuthMethod: "api-key",
            Env: new JsonObject { ["CODEX_HOME"] = ContainerCodexHome },
            Telemetry: null),
        "grok" => new(
            ["grok", "--model", GrokModel(), "agent", "stdio"],
            Prompt("landbridge__get_inbox", "landbridge__report_result", "landbridge__request_input"),
            FollowUp("landbridge__get_inbox"),
            AuthMethod: null,
            Env: new JsonObject
            {
                ["GROK_FOLDER_TRUST"] = "0",
                ["GROK_DEFAULT_MODEL"] = GrokModel(),
            },
            Telemetry: null,
            ConfigOptions: new JsonObject { ["model"] = GrokModel() }),
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
        if (recipe.ConfigOptions is not null)
            profile["config_options"] = recipe.ConfigOptions.DeepClone();
        return profile;
    }

    private static string Prompt(string getSession, string reportResult, string requestInput) =>
        $"You are a Landbridge worker on a live session. Read the landbridge-worker skill " +
        $"from the landbridge MCP server first (`landbridge://skills/worker`). Do not search " +
        $"$HOME or ~/.claude for a skill. Then call the {getSession} MCP tool to read your " +
        "assignment (namespace, description, attempt). Do the work in this " +
        "session's directory; you are not the only agent on the machine. You must not end " +
        $"a turn until you have called {reportResult} or {requestInput}. When you think you " +
        $"are done, call {reportResult} with a reference to where the work lives (a " +
        "branch/commit/URL) — not the work itself — and stay up; the Lead may reply. If " +
        $"you are blocked or a decision is above your scope, call {requestInput} instead " +
        "of guessing. You do not complete the session yourself.";

    private static string FollowUp(string getSession) =>
        $"There is new input on your assignment. Call {getSession} to read it, then continue.";
}
