using Landbridge.Runner;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// The real-CLI fixtures. What differs per harness is the ACP entry point
/// and how that vendor spells landbridge's MCP tools. MCP itself rides
/// <c>session/new</c>; these fixtures do not write a bearer file.
/// </summary>
internal static class RealHarnessProfiles
{
    public static readonly string[] CodexBareTools = ["get_inbox", "report_result", "request_input"];

    public static RealHarnessProfile Claude(string bin) => new()
    {
        Name = "claude",
        // The adapter, not `claude`: @agentclientprotocol/claude-agent-acp (the
        // @zed-industries/claude-code-acp name is deprecated). Measured 2026-08-15:
        // protocol 1, loadSession true, mcpCapabilities.http true, ambient auth.
        AcpSpawn = ["claude-agent-acp"],
        Bin = bin,
        GetTask = "mcp__landbridge__get_inbox",
        ReportResult = "mcp__landbridge__report_result",
        RequestInput = "mcp__landbridge__request_input",
        Usage = UsageExpectation.Cost,
        // NamesModel deliberately off under ACP, unlike the stream profile. Nothing in the
        // protocol attributes usage to a model — not PromptResponse.usage, not usage_update
        // — so the plane records the spend unattributed rather than guessing from argv that
        // names an adapter. Measured 2026-08-16 on a real turn: cost $0.09490875 arrived, a
        // model name did not.
        SupportsResume = true,
    };

    public static RealHarnessProfile Codex(string bin) => new()
    {
        Name = "codex",
        // @agentclientprotocol/codex-acp. Measured 2026-08-15: protocol 1, loadSession
        // true, mcp.http true. Note what this removes against the stream profile —
        // codex exec needs stdin: closed to start at all, and codex-acp does not.
        AcpSpawn = ["codex-acp"],
        AuthMethod = "api-key",
        Bin = bin,
        GetTask = "mcp__landbridge__get_inbox",
        ReportResult = "mcp__landbridge__report_result",
        RequestInput = "mcp__landbridge__request_input",
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
        ConfigOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = OpenCodeModel,
        },
        Bin = bin,
        GetTask = "landbridge_get_inbox",
        ReportResult = "landbridge_report_result",
        RequestInput = "landbridge_request_input",
        // Tokens required. Cost is optional: Anthropic-pinned ACP reports one,
        // the previous big-pickle default reported none. A stored $0.00 is still
        // forbidden — that would claim the dispatch was free.
        Usage = UsageExpectation.Tokens,
        SupportsResume = true,
        FailureHypotheses = OpenCodeHypotheses(),
        Attach = rig => OpenCodeConfig.Create(rig.McpUrl),
    };

    public static RealHarnessProfile Grok(string bin) => new()
    {
        Name = "grok",
        // Native. `grok agent stdio`, NOT `-p --output-format streaming-json`.
        // GROK_DEFAULT_MODEL is the pin grok actually reads. --model on argv
        // is ignored by `agent stdio` on 1.0.3: measured #222, spawn was
        // `grok --model grok-4.6 agent stdio` and the session still sat on
        // grok-4.20-0309-non-reasoning, which narrated tools and died
        // TurnEndedWithoutResult on park/resume. ACP config_options is
        // skipped unless advertised (same as Codex). LANDBRIDGE_GROK_MODEL
        // is the Landbridge override; the CI default is grok-4.6.
        AcpSpawn = [bin, "--model", GrokModel, "agent", "stdio"],
        Bin = bin,
        ConfigOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = GrokModel,
        },
        // 1.0.4 gates project-local config behind folder trust. A landbridged work
        // dir is a throwaway temp folder, so disable the gate.
        Env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GROK_FOLDER_TRUST"] = "0",
            ["GROK_DEFAULT_MODEL"] = GrokModel,
        },
        GetTask = "landbridge__get_inbox",
        ReportResult = "landbridge__report_result",
        RequestInput = "landbridge__request_input",
        // Tokens, not Cost. Measured 2026-08-16: grok agent stdio reports spend on
        // `_x.ai/session_notification` / `response_completed` as snake_case buckets
        // and no dollar figure. Recording $0.00 would claim the dispatch was free.
        Usage = UsageExpectation.Tokens,
        SupportsResume = true,
        FailureHypotheses = GrokHypotheses(),
    };

    public static RealHarnessProfile Goose(string bin) => new()
    {
        Name = "goose",
        // Native. Handshake captured on Goose 1.37.0: protocol 1, loadSession true,
        // mcp.http true. authMethods lists goose-provider (interactive configure);
        // session/new succeeded without authenticate. Do not set AuthMethod.
        AcpSpawn = [bin, "acp"],
        Bin = bin,
        GetTask = "landbridge__get_inbox",
        ReportResult = "landbridge__report_result",
        RequestInput = "landbridge__request_input",
        SessionMode = "approve",
        // Tokens, not Cost. Measured 2026-08-17 through the ACP-bridge turn:
        // PromptResponse carried buckets. A stored $0.00 is still forbidden.
        Usage = UsageExpectation.Tokens,
        SupportsResume = true,
        FailureHypotheses = GooseHypotheses(),
    };

    public static RealHarnessProfile GooseViaBridge(string gooseBin, string bridgeBin, string url) => new()
    {
        Name = "goose-acp-bridge",
        AcpSpawn = [bridgeBin, "connect", url],
        Bin = gooseBin,
        GetTask = "landbridge__get_inbox",
        ReportResult = "landbridge__report_result",
        RequestInput = "landbridge__request_input",
        SessionMode = "approve",
        // Tokens, not Cost: first live turn will say whether PromptResponse
        // carries buckets. A stored $0.00 is still forbidden.
        Usage = UsageExpectation.Tokens,
        SupportsResume = true,
        FailureHypotheses = GooseHypotheses()
            + "  6. BRIDGE. Far side is `landbridge-acp-bridge listen -- goose acp`. "
            + "Connect spawn is the profile. A 409 means two dispatches shared one listen.",
    };

    public static string CodexModel =>
        Environment.GetEnvironmentVariable("LANDBRIDGE_CODEX_MODEL") is { Length: > 0 } m ? m : "gpt-5.3-codex";

    public static string OpenCodeModel =>
        Environment.GetEnvironmentVariable("LANDBRIDGE_OPENCODE_MODEL") is { Length: > 0 } m
            ? m
            : "anthropic/claude-haiku-4-5-20251001";

    public static string GrokModel =>
        Environment.GetEnvironmentVariable("LANDBRIDGE_GROK_MODEL") is { Length: > 0 } m ? m : "grok-4.6";

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

    /// <summary>
    /// Opt-in flags like <c>LANDBRIDGE_REAL_CLAUDE</c> / <c>LANDBRIDGE_REAL_GOOSE</c>.
    /// Empty, <c>0</c>, and <c>false</c> are off.
    /// </summary>
    public static bool EnvFlag(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } o
        && !o.Equals("0", StringComparison.Ordinal)
        && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

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
    /// A throwaway <c>CODEX_HOME</c> holding the static landbridge MCP table. One file
    /// is enough: the bearer rides <c>LANDBRIDGE_WORKER_TOKEN</c> at connect time.
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
            var dir = Path.Combine(Path.GetTempPath(), "landbridge-codex-home-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            var tools = string.Join(", ", allowedTools.Select(t => $"\"{t}\""));
            File.WriteAllText(
                Path.Combine(dir, "config.toml"),
                $"""
                 model = "{CodexModel}"

                 [mcp_servers.landbridge]
                 url = "{mcpUrl}"
                 bearer_token_env_var = "LANDBRIDGE_WORKER_TOKEN"
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
    /// bearer is <c>{env:LANDBRIDGE_WORKER_TOKEN}</c>; <c>"oauth": false</c> is required.
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
            var dir = Path.Combine(Path.GetTempPath(), "landbridge-opencode-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "opencode.json");

            File.WriteAllText(
                file,
                $$"""
                  {
                    "$schema": "https://opencode.ai/config.json",
                    "model": "{{OpenCodeModel}}",
                    "mcp": {
                      "landbridge": {
                        "type": "remote",
                        "url": "{{mcpUrl}}",
                        "enabled": true,
                        "headers": { "Authorization": "Bearer {env:LANDBRIDGE_WORKER_TOKEN}" },
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
        $"""

        Suspect, in order:
          1. MODEL SLUG. This tier pins '{CodexModel}'. gpt-5.1-codex-mini 404s on
             the API-key catalog; override with LANDBRIDGE_CODEX_MODEL.
          2. MCP WIRING. CODEX_HOME/config.toml uses bearer_token_env_var = LANDBRIDGE_WORKER_TOKEN
             and required = true. A plane 401 or a missing table fails the run.
          3. STDIN. A deadman profile hangs before the first turn. Bar facts declare closed.
          4. AUTH. CODEX_API_KEY (not OPENAI_API_KEY) must be in the environment.

        """;

    private static string OpenCodeHypotheses() =>
        $$"""

         Suspect, in order:
           1. MODEL SLUG. This tier pins '{{OpenCodeModel}}'. Override with LANDBRIDGE_OPENCODE_MODEL.
           2. MCP WIRING. The bearer arrives via {env:LANDBRIDGE_WORKER_TOKEN}. An unset variable
              substitutes to the empty string, so the plane 401s and the agent has no tools.
           3. TOOL NAMES. OpenCode spells MCP tools landbridge_get_inbox, not mcp__landbridge__get_inbox.
           4. STDIN. A deadman profile hangs silently before the first turn.
           5. AUTH. ANTHROPIC_API_KEY must be in the environment.

         """;

    private static string GrokHypotheses() =>
        $$"""

         Suspect, in order:
           1. MODEL SLUG. GROK_DEFAULT_MODEL={{GrokModel}} is the pin grok
              agent stdio reads. --model on argv is ignored on 1.0.3 (#222
              still sat on grok-4.20-0309-non-reasoning). config_options.model
              is the same slug, skipped if unadvertised. Override with
              LANDBRIDGE_GROK_MODEL.
           2. MCP WIRING. The plane is handed over on session/new. A 401 means the
              minted token or url is wrong. GROK_FOLDER_TRUST=0 is set so a throwaway
              work dir is not blocked by folder trust.
           3. TOOL NAMES. Grok spells MCP tools landbridge__get_inbox, not mcp__landbridge__get_inbox.
           4. STDIN. A deadman profile starts then never exits. Bar facts declare closed.
           5. AUTH. XAI_API_KEY (not XAI_KEY) must be in the environment.

         """;

    private static string GooseHypotheses() =>
        """

        Suspect, in order:
          1. ENTRY POINT. Spawn is `goose acp`, not `goose serve` and not `goose run`.
          2. AUTH. goose-provider is interactive `goose configure`. The process needs
             a provider already configured, or GOOSE_PROVIDER / GOOSE_MODEL plus that
             provider's key. Do not set auth_method to goose-provider.
          3. TOOL NAMES. Expected spelling is landbridge__get_inbox (goose namespaces the
             `landbridge` MCP server as `{name}__{tool}`). Confirm on the first turn.
          4. MODE. session/new defaults to `auto`. This profile pins `approve`
             via session/set_mode when the session advertised it.
          5. FS/TERMINAL. This client declares both UNSUPPORTED. A Goose that asks
             for terminal/create cannot work here.

        """;
}
