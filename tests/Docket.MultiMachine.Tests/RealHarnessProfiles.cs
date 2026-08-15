using Docket.Runner;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// The four real-CLI fixtures. Spawn argv, mapping, files, and attach helpers are
/// the same recipes the per-harness characterization files already used — lifted
/// here so the shared bar and those files do not drift.
/// </summary>
internal static class RealHarnessProfiles
{
    public const string ClaudeMcpPath = "{work_dir}/mcp-{task_id}.json";

    public static readonly ProfileFile[] ClaudeMcpFile =
    [
        new(
            ClaudeMcpPath,
            """{"mcpServers":{"docket":{"type":"http","url":"{mcp_url}","headers":{"Authorization":"Bearer {worker_token}"}}}}"""),
    ];

    public static readonly ProfileFile[] GrokMcpFile =
    [
        new(
            "{work_dir}/.grok/config.toml",
            """
            [mcp_servers.docket]
            url = "{mcp_url}"
            enabled = true
            headers = { "Authorization" = "Bearer {worker_token}" }
            """,
            "600"),
    ];

    public static readonly Dictionary<string, string> CodexEventMapping = new()
    {
        ["system_type"] = "thread.started",
        ["subtype_key"] = "type",
        ["init_subtype"] = "thread.started",
        ["session_id_key"] = "thread_id",
        ["tool_event_type"] = "item.started",
        ["tool_name_path"] = "item.command, item.tool",
        ["usage_type"] = "turn.completed",
        ["usage_cache_read_key"] = "cached_input_tokens",
        ["usage_cache_write_key"] = "cache_write_input_tokens",
        ["usage_reasoning_key"] = "reasoning_output_tokens",
        ["usage_cost_key"] = "",
        ["usage_models_key"] = "",
        ["usage_cached_is_subset"] = "true",
        ["usage_is_cumulative"] = "false",
    };

    public static readonly Dictionary<string, string> OpenCodeEventMapping = new()
    {
        ["system_type"] = "step_start",
        ["subtype_key"] = "type",
        ["init_subtype"] = "step_start",
        ["session_id_key"] = "sessionID",
        ["tool_event_type"] = "tool_use",
        ["tool_name_path"] = "part.tool",
        ["usage_type"] = "step_finish",
        ["usage_key"] = "part.tokens",
        ["usage_input_key"] = "input",
        ["usage_output_key"] = "output",
        ["usage_cache_read_key"] = "cache.read",
        ["usage_cache_write_key"] = "cache.write",
        ["usage_reasoning_key"] = "reasoning",
        ["usage_cost_key"] = "part.cost",
        ["usage_models_key"] = "",
        ["usage_cached_is_subset"] = "false",
        ["usage_reasoning_is_subset"] = "false",
        ["usage_is_cumulative"] = "false",
    };

    public static readonly string[] CodexBareTools = ["get_task", "report_result", "request_input"];

    public static RealHarnessProfile Claude(string bin) => new()
    {
        Name = "claude",
        // The adapter, not `claude`: @agentclientprotocol/claude-agent-acp (the
        // @zed-industries/claude-code-acp name is deprecated). Measured 2026-08-15:
        // protocol 1, loadSession true, mcpCapabilities.http true, ambient auth.
        AcpSpawn = ["claude-agent-acp"],
        Bin = bin,
        Files = ClaudeMcpFile,
        GetTask = "mcp__docket__get_task",
        ReportResult = "mcp__docket__report_result",
        RequestInput = "mcp__docket__request_input",
        Usage = UsageExpectation.Cost,
        NamesModel = true,
        SupportsResume = true,
    };

    public static RealHarnessProfile Codex(string bin) => new()
    {
        Name = "codex",
        // @agentclientprotocol/codex-acp. Measured 2026-08-15: protocol 1, loadSession
        // true, mcp.http true. Note what this removes against the stream profile —
        // codex exec needs stdin: closed to start at all, and codex-acp does not.
        AcpSpawn = ["codex-acp"],
        Bin = bin,
        GetTask = "mcp__docket__get_task",
        ReportResult = "mcp__docket__report_result",
        RequestInput = "mcp__docket__request_input",
        Usage = UsageExpectation.Tokens,
        SupportsResume = true,
        FailureHypotheses = CodexHypotheses(),
        Attach = rig => CodexHome.Create(rig.McpUrl, CodexBareTools),
    };

    public static RealHarnessProfile OpenCode(string bin) => new()
    {
        Name = "opencode",
        // Native. Measured 2026-08-15: protocol 1, loadSession true, mcp.http true.
        AcpSpawn = [bin, "acp"],
        Bin = bin,
        GetTask = "docket_get_task",
        ReportResult = "docket_report_result",
        RequestInput = "docket_request_input",
        Usage = UsageExpectation.Cost,
        SupportsResume = true,
        FailureHypotheses = OpenCodeHypotheses(),
        Attach = rig => OpenCodeConfig.Create(rig.McpUrl),
    };

    public static RealHarnessProfile Grok(string bin) => new()
    {
        Name = "grok",
        // Native, per xAI docs — the one entry point in this file NOT measured, because
        // grok installs through the GitHub API. `grok agent stdio`, NOT
        // `-p --output-format streaming-json`, which is an output shape not the protocol.
        AcpSpawn = [bin, "agent", "stdio"],
        Bin = bin,
        Files = GrokMcpFile,
        // 1.0.4 gates project-local MCP (the files[] config.toml) behind folder
        // trust. A docketd work dir is a throwaway temp folder, so disable the
        // gate rather than writing ~/.grok/trusted_folders.toml per task.
        Env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GROK_FOLDER_TRUST"] = "0",
        },
        GetTask = "docket__get_task",
        ReportResult = "docket__report_result",
        RequestInput = "docket__request_input",
        Usage = UsageExpectation.Cost,
        SupportsResume = true,
        FailureHypotheses = GrokHypotheses(),
    };

    public static string[] ClaudeSpawn(string bin, string prompt, string tools, params string[] extra) =>
    [
        bin, "-p", prompt,
        "--mcp-config", ClaudeMcpPath,
        "--strict-mcp-config",
        "--allowedTools", tools,
        "--output-format", "stream-json",
        "--verbose",
        "--model", "haiku",
        "--max-turns", "8",
        .. extra,
    ];

    public static string[] CodexSpawn(string bin, string prompt, params string[] extra)
    {
        var argv = new List<string>
        {
            bin, "exec", prompt,
            "--json",
            "--skip-git-repo-check",
            "--dangerously-bypass-approvals-and-sandbox",
            "--model", CodexModel,
        };
        argv.AddRange(extra);
        return [.. argv];
    }

    public static string[] CodexResume(string bin, string prompt) =>
    [
        bin, "exec", "resume", "{session_id}", prompt,
        "--json",
        "--skip-git-repo-check",
        "--dangerously-bypass-approvals-and-sandbox",
        "--model", CodexModel,
    ];

    public static string[] OpenCodeSpawn(string bin, string prompt, params string[] extra) =>
    [
        bin, "run", prompt,
        "--format", "json",
        "--auto",
        "--model", OpenCodeModel,
        .. extra,
    ];

    public static string[] OpenCodeResume(string bin, string prompt) =>
    [
        bin, "run", prompt,
        "--session", "{session_id}",
        "--format", "json",
        "--auto",
        "--model", OpenCodeModel,
    ];

    public static string[] GrokSpawn(string bin, string prompt, params string[] extra) =>
    [
        bin, "-p", prompt,
        "--output-format", "streaming-messages-json",
        "--always-approve",
        "--no-auto-update",
        "--max-turns", "8",
        "-m", GrokModel,
        .. extra,
    ];

    public static string[] GrokResume(string bin, string prompt) =>
    [
        bin, "-p", prompt,
        "--resume", "{session_id}",
        "--output-format", "streaming-messages-json",
        "--always-approve",
        "--no-auto-update",
        "-m", GrokModel,
    ];

    public static string CodexModel =>
        Environment.GetEnvironmentVariable("DOCKET_CODEX_MODEL") is { Length: > 0 } m ? m : "gpt-5.1-codex-mini";

    public static string OpenCodeModel =>
        Environment.GetEnvironmentVariable("DOCKET_OPENCODE_MODEL") is { Length: > 0 } m
            ? m
            : "anthropic/claude-haiku-4-5-20251001";

    public static string GrokModel =>
        Environment.GetEnvironmentVariable("DOCKET_GROK_MODEL") is { Length: > 0 } m ? m : "grok-4.6";

    public static string EchoDescription(string label, string token) =>
        $"""
         Call report_result exactly once, with its resultReference set to this exact string
         (no quotes, no other text):

         {label}:{token}

         That is the entire task. Do not create or edit files. Do not do anything else.
         """;

    public static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    public static string? FirstNonEmpty(params string[] names)
    {
        foreach (var name in names)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    public static string? ResolveBin(string name, string overrideVar)
    {
        var explicitBin = Environment.GetEnvironmentVariable(overrideVar);
        if (!string.IsNullOrWhiteSpace(explicitBin) && File.Exists(explicitBin))
            return explicitBin;

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
                     $"/usr/local/bin/{name}",
                     $"/opt/homebrew/bin/{name}",
                     Path.Combine(home, ".grok", "bin", exe),
                 })
            if (File.Exists(fallback)) return fallback;

        return null;
    }

    public static void ScrubInheritedSessionMarkers()
    {
        foreach (var name in InheritedSessionMarkers)
            Environment.SetEnvironmentVariable(name, null);
    }

    private static readonly string[] InheritedSessionMarkers =
    [
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS",
        "CLAUDE_PID",
        "CLAUDE_EFFORT",
        "AI_AGENT",
    ];

    /// <summary>
    /// A throwaway <c>CODEX_HOME</c> holding the static docket MCP table. One file
    /// is enough: the bearer rides <c>DOCKET_WORKER_TOKEN</c> at connect time.
    /// </summary>
    internal sealed class CodexHome : IDisposable
    {
        private readonly string _dir;
        private readonly string? _previous;

        private CodexHome(string dir, string? previous)
        {
            _dir = dir;
            _previous = previous;
        }

        public static CodexHome Create(string mcpUrl, IReadOnlyList<string> allowedTools)
        {
            var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
            var dir = Path.Combine(Path.GetTempPath(), "docket-codex-home-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            var tools = string.Join(", ", allowedTools.Select(t => $"\"{t}\""));
            File.WriteAllText(
                Path.Combine(dir, "config.toml"),
                $"""
                 [mcp_servers.docket]
                 url = "{mcpUrl}"
                 bearer_token_env_var = "DOCKET_WORKER_TOKEN"
                 enabled_tools = [{tools}]
                 required = true
                 startup_timeout_sec = 30.0
                 tool_timeout_sec = 120.0

                 """);

            if (Environment.GetEnvironmentVariable("CODEX_API_KEY") is not { Length: > 0 })
            {
                var operatorAuth = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
                var seeded = Path.Combine(dir, "auth.json");
                if (File.Exists(operatorAuth) && !File.Exists(seeded))
                    try { File.Copy(operatorAuth, seeded); } catch { /* best effort */ }
            }

            Environment.SetEnvironmentVariable("CODEX_HOME", dir);
            return new CodexHome(dir, previous);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", _previous);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A throwaway OpenCode config published through <c>OPENCODE_CONFIG</c>. The
    /// bearer is <c>{env:DOCKET_WORKER_TOKEN}</c>; <c>"oauth": false</c> is required.
    /// </summary>
    internal sealed class OpenCodeConfig : IDisposable
    {
        private readonly string _dir;
        private readonly string? _previous;

        private OpenCodeConfig(string dir, string? previous)
        {
            _dir = dir;
            _previous = previous;
        }

        public static OpenCodeConfig Create(string mcpUrl)
        {
            var previous = Environment.GetEnvironmentVariable("OPENCODE_CONFIG");
            var dir = Path.Combine(Path.GetTempPath(), "docket-opencode-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "opencode.json");

            File.WriteAllText(
                file,
                $$"""
                  {
                    "$schema": "https://opencode.ai/config.json",
                    "mcp": {
                      "docket": {
                        "type": "remote",
                        "url": "{{mcpUrl}}",
                        "enabled": true,
                        "headers": { "Authorization": "Bearer {env:DOCKET_WORKER_TOKEN}" },
                        "oauth": false,
                        "timeout": 120000
                      }
                    }
                  }
                  """);

            Environment.SetEnvironmentVariable("OPENCODE_CONFIG", file);
            return new OpenCodeConfig(dir, previous);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG", _previous);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string CodexHypotheses() =>
        """

        Suspect, in order:
          1. MODEL SLUG. Override with DOCKET_CODEX_MODEL.
          2. MCP WIRING. CODEX_HOME/config.toml uses bearer_token_env_var = DOCKET_WORKER_TOKEN
             and required = true. A plane 401 or a missing table fails the run.
          3. STDIN. A deadman profile hangs before the first turn. Bar facts declare closed.
          4. AUTH. CODEX_API_KEY (not OPENAI_API_KEY) must be in the environment.

        """;

    private static string OpenCodeHypotheses() =>
        $$"""

         Suspect, in order:
           1. MODEL SLUG. This tier pins '{{OpenCodeModel}}'. Override with DOCKET_OPENCODE_MODEL.
           2. MCP WIRING. The bearer arrives via {env:DOCKET_WORKER_TOKEN}. An unset variable
              substitutes to the empty string, so the plane 401s and the agent has no tools.
           3. TOOL NAMES. OpenCode spells MCP tools docket_get_task, not mcp__docket__get_task.
           4. STDIN. A deadman profile hangs silently before the first turn.
           5. AUTH. ANTHROPIC_API_KEY must be in the environment.

         """;

    private static string GrokHypotheses() =>
        $$"""

         Suspect, in order:
           1. MODEL SLUG. This tier pins '{{GrokModel}}'. Override with DOCKET_GROK_MODEL.
           2. MCP WIRING. files[] writes {work_dir}/.grok/config.toml with {mcp_url}
              and Bearer {worker_token}, both docketd file-substitutions written
              verbatim (grok does NOT expand ${ENV} in config.toml). A 401 here means
              the minted token or url is wrong. If search_tool says "No MCP tools are
              available" with mcp_wait_ms=0, folder trust blocked the project file —
              this profile sets GROK_FOLDER_TRUST=0 for that reason.
           3. TOOL NAMES. Grok spells MCP tools docket__get_task, not mcp__docket__get_task.
           4. STDIN. A deadman profile starts then never exits. Bar facts declare closed.
           5. AUTH. XAI_API_KEY (not XAI_KEY) must be in the environment.

         """;
}
