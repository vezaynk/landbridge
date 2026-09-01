using Landbridge.Observability.Models;

namespace Landbridge.Observability.Services;

/// <summary>
/// Fake fleet data — same session names, machines and vocabulary as the
/// "1a lane board" mockup, so the sample page reproduces it directly.
/// </summary>
internal static class SeedData
{
    // A tiny deterministic LCG, mirroring the mockup's `rand(seed)` helper,
    // so re-seeding the same agent always draws the same initial timeline.
    private sealed class Lcg(int seed)
    {
        private int _s = seed;
        public double Next()
        {
            _s = (int)((_s * 9301L + 49297L) % 233280L);
            return _s / 233280.0;
        }
    }

    private sealed record AgentSeed(
        string Ns, string Profile, string Machine, SessionState State,
        string Now, string Elapsed, string Attempt, int Unread, int Seed,
        (string Name, int Port, bool Live)[] Ports);

    private static readonly AgentSeed[] Agents =
    [
        new("sess/rel-splice-fix", "codex", "codex-apphost-linux", SessionState.Working,
            "Edit — src/Landbridge.Relay/ForwardRegistry.cs", "2m14s", "a1", 0, 3,
            [("relay-dev", 5100, true)]),
        new("sess/preview-tls-renew", "claude", "claude-apphost-linux", SessionState.Permission,
            "Bash — lego renew --dns cloudflare", "46s", "a1", 1, 11, []),
        new("sess/dash-inbox-perms", "claude", "mac-studio-01", SessionState.Working,
            "Bash — dotnet test Landbridge.Mcp.Tests", "4m02s", "a2", 2, 5,
            [("dash", 5050, true), ("pg", 5432, false)]),
        new("sess/runner-stray-sweep", "grok", "grok-apphost-linux", SessionState.Working,
            "Read — /proc/<pid>/environ", "38s", "a1", 0, 17, []),
        new("sess/meta-saga-resume", "codex", "hetzner-de-03", SessionState.Question,
            "Asked the Lead — which Postgres tag?", "6m11s", "a1", 1, 23, []),
        new("sess/token-ramp-audit", "claude", "mac-studio-01", SessionState.Working,
            "Grep — TokenService.Revoke", "1m07s", "a1", 0, 29, []),
        new("sess/classifier-argv", "opencode", "—", SessionState.Submitted,
            "Queued — no machine declares opencode-linux", "3m50s", "—", 0, 31, []),
        new("sess/relay-byte-counter", "codex", "codex-apphost-linux", SessionState.Working,
            "Wrote report — waiting on Lead review", "22s", "a1", 1, 37,
            [("bench", 8080, true)]),
        new("sess/os-matrix-windows", "claude", "win-box-02", SessionState.Failed,
            "No progress for 12m — requeue 4 of 5", "12m30s", "a4", 0, 41, []),
        new("sess/transcript-serving", "grok", "grok-apphost-linux", SessionState.Parked,
            "Parked — question outlived its lease", "31m", "a2", 1, 43, []),
        new("sess/harness-bump-rules", "codex", "hetzner-de-03", SessionState.Completed,
            "Closed by Lead — PR #214", "8m", "a1", 0, 47, []),
        new("sess/enroll-wizard-spike", "claude", "mac-studio-01", SessionState.Working,
            "Write — docs/ENROLL.md", "55s", "a1", 0, 53, []),
        new("sess/preview-label-mint", "goose", "mac-mini-04", SessionState.Working,
            "Serving preview — h7q2.preview.lb.dev", "9m40s", "a1", 0, 59,
            [("web", 3000, true)]),
        new("sess/acp-bridge-conform", "claude", "win-box-02", SessionState.Working,
            "Bash — acp-bridge --probe", "1m48s", "a1", 0, 61, []),
    ];

    public static List<Machine> BuildMachines() =>
    [
        new() { Id = "codex-apphost-linux", LoadText = "2 · 41%" },
        new() { Id = "claude-apphost-linux", LoadText = "1 · 22%" },
        new() { Id = "grok-apphost-linux", LoadText = "2 · 58%" },
        new() { Id = "mac-studio-01", LoadText = "3 · 77%" },
        new() { Id = "hetzner-de-03", LoadText = "back-pressure", BackPressure = true },
        new() { Id = "win-box-02", LoadText = "2 · 35%" },
        new() { Id = "mac-mini-04", LoadText = "1 · 12%" },
    ];

    public static List<AgentSession> BuildAgents()
    {
        var list = new List<AgentSession>();
        for (var i = 0; i < Agents.Length; i++)
        {
            var s = Agents[i];
            var elapsedSeconds = ParseElapsed(s.Elapsed);
            var hasDispatch = s.State != SessionState.Submitted;

            var a = new AgentSession
            {
                Id = i,
                Ns = s.Ns,
                Profile = s.Profile,
                Machine = s.Machine,
                State = s.State,
                Now = s.Now,
                ElapsedSeconds = elapsedSeconds,
                Attempt = s.Attempt,
                Unread = s.Unread,
                Seed = s.Seed,
                Ports = [.. s.Ports.Select(p => new PortInfo { Name = p.Name, Port = p.Port, Live = p.Live })],
            };

            a.Events.AddRange(BuildEvents(s));

            var tokensK = hasDispatch ? 40 + s.Seed * 13 % 260 : 0;
            a.Tokens = tokensK * 1000L;
            a.CostUsd = hasDispatch && a.ReportsDollars ? tokensK * 0.0061 : null;
            a.UsageInPct = hasDispatch ? 20 + s.Seed % 11 : 0;
            a.UsageOutPct = hasDispatch ? 12 + s.Seed % 8 : 0;
            a.UsageCachePct = hasDispatch ? 68 - s.Seed % 11 - s.Seed % 8 : 0;
            a.ToolCalls = 12 + s.Seed % 40;
            a.RateTokPerMin = a.Meta.Live && s.State != SessionState.Failed ? 1 + s.Seed % 9 / 2.0 : null;
            a.MinutesSinceReport = 1 + s.Seed % 9;

            a.Messages.AddRange(BuildMessages(s));
            a.Transcript.AddRange(BuildTranscript(s));

            list.Add(a);
        }
        return list;
    }

    private static int ParseElapsed(string text)
    {
        int h = 0, m = 0, sec = 0;
        var num = "";
        foreach (var c in text)
        {
            if (char.IsDigit(c)) { num += c; continue; }
            switch (c)
            {
                case 'h': h = int.Parse(num); break;
                case 'm': m = int.Parse(num); break;
                case 's': sec = int.Parse(num); break;
            }
            num = "";
        }
        return h * 3600 + m * 60 + sec;
    }

    private static IEnumerable<TimelineMark> BuildEvents(AgentSeed a)
    {
        var rng = new Lcg(a.Seed);
        var marks = new List<TimelineMark>
        {
            new() { Type = MarkType.Dispatch, PositionPct = 3 },
        };

        var n = 8 + (int)(rng.Next() * 9);
        for (var i = 0; i < n; i++)
            marks.Add(new TimelineMark { Type = MarkType.Tool, PositionPct = 6 + rng.Next() * 90 });

        if (a.State is SessionState.Working or SessionState.Completed)
        {
            marks.Add(new TimelineMark { Type = MarkType.Ask, PositionPct = 20 + rng.Next() * 30 });
            marks.Add(new TimelineMark { Type = MarkType.Answer, PositionPct = 55 + rng.Next() * 20 });
        }
        if (a.State == SessionState.Permission)
            marks.Add(new TimelineMark { Type = MarkType.Ask, PositionPct = 96 });
        if (a.State is SessionState.Question or SessionState.Parked)
            marks.Add(new TimelineMark { Type = MarkType.Ask, PositionPct = 62 + rng.Next() * 10 });
        if (a.State == SessionState.Parked)
            marks.Add(new TimelineMark { Type = MarkType.Park, PositionPct = 92 });
        if (a.State == SessionState.Failed)
        {
            marks.Add(new TimelineMark { Type = MarkType.Error, PositionPct = 58 });
            marks.Add(new TimelineMark { Type = MarkType.Error, PositionPct = 74 });
            marks.Add(new TimelineMark { Type = MarkType.Dispatch, PositionPct = 80 });
        }
        if (a.State == SessionState.Completed)
            marks.Add(new TimelineMark { Type = MarkType.Done, PositionPct = 94 });
        if (a.Ports.Length > 0)
            marks.Add(new TimelineMark { Type = MarkType.Forward, PositionPct = 40 + rng.Next() * 15, WidthPct = 30 + rng.Next() * 25 });

        return marks;
    }

    // The dash-inbox-perms session keeps the hand-written exchange the mockup shipped with —
    // it is that session's own story (it registers `dash` on port 5050, which is exactly the
    // port that session's row has open). Every other lane gets content generated from its own
    // "now" text, so picking any row shows something that actually belongs to it.
    private static IEnumerable<ChatMessage> BuildMessages(AgentSeed s)
    {
        if (s.Ns == "sess/dash-inbox-perms")
            return
            [
                new() { From = "Worker", At = "14:22:06", Text = "Registered service `dash` on 5050. Splice tests pass locally; the Windows leg still skips." },
                new() { From = "Lead", At = "14:22:41", Text = "Skips are fine — SkippableFact is the documented deferral. Push and open the PR." },
                new() { From = "Worker", At = "14:24:18", Text = "May I run `dotnet test` against the shared Postgres container? It is outside the argv allowlist." },
            ];

        var t1 = "14:1" + (s.Seed % 10) + ":0" + (s.Seed % 6);
        var t2 = "14:2" + (s.Seed % 10) + ":1" + (s.Seed % 6);
        return s.State switch
        {
            SessionState.Permission =>
            [
                new() { From = "Worker", At = t1, Text = $"May I proceed? `{s.Now.Replace("Bash — ", "")}` falls outside the argv allowlist." },
            ],
            SessionState.Question or SessionState.Parked =>
            [
                new() { From = "Worker", At = t1, Text = s.Now.Replace("Asked the Lead — ", "").Replace("Parked — ", "") },
            ],
            SessionState.Failed =>
            [
                new() { From = "Worker", At = t1, Text = $"{s.Now}." },
                new() { From = "Lead", At = t2, Text = $"Noted — flag it if {s.Attempt} also stalls." },
            ],
            SessionState.Completed =>
            [
                new() { From = "Worker", At = t1, Text = s.Now },
                new() { From = "Lead", At = t2, Text = "Nice work — closing the loop." },
            ],
            SessionState.Submitted =>
            [
                new() { From = "Lead", At = t1, Text = s.Now },
            ],
            _ =>
            [
                new() { From = "Worker", At = t1, Text = s.Now },
            ],
        };
    }

    private static readonly (string Verb, string Path)[] ToolPool =
    [
        ("Read", "src/Landbridge.Core/SessionState.cs"),
        ("Grep", "\"MessageState\" (9 hits)"),
        ("Bash", "dotnet build -c Release"),
        ("Edit", "ForwardRegistry.cs +12 −3"),
        ("Write", "docs/RUNBOOK.md"),
        ("Bash", "git diff --stat"),
    ];

    private static IEnumerable<TranscriptLine> BuildTranscript(AgentSeed s)
    {
        var rng = new Lcg(s.Seed);
        var lines = new List<TranscriptLine>();
        var baseMinute = 20 + s.Seed % 30;

        string Time(int offsetSec) => $"14:{baseMinute:00}:{offsetSec % 60:00}";

        var n = 3 + s.Seed % 3;
        for (var i = 0; i < n; i++)
        {
            var (verb, path) = ToolPool[(int)(rng.Next() * ToolPool.Length)];
            lines.Add(new TranscriptLine { Time = Time(i * 4), Text = $"tool-call  {verb} {path}", Kind = TranscriptLineKind.ToolCall });
        }

        lines.Add(new TranscriptLine { Time = Time(n * 4), Text = s.Now, Kind = TranscriptLineKind.Narration });

        if (s.State == SessionState.Permission)
        {
            lines.Add(new TranscriptLine { Time = Time(n * 4 + 3), Text = "session/request_permission  " + s.Now.Replace("Bash — ", "Bash("), Kind = TranscriptLineKind.Waiting });
            lines.Add(new TranscriptLine { Time = Time(n * 4 + 3), Text = "classifier → ask  (argv allowlist: no match)", Kind = TranscriptLineKind.Waiting });
            lines.Add(new TranscriptLine { Time = Time(n * 4 + 5), Text = $"waiting on a verdict — {s.Elapsed}", Kind = TranscriptLineKind.Waiting });
        }
        else if (s.State == SessionState.Failed)
        {
            lines.Add(new TranscriptLine { Time = Time(n * 4 + 3), Text = "runner/liveness_lost  no heartbeat in 45s", Kind = TranscriptLineKind.Waiting });
            lines.Add(new TranscriptLine { Time = Time(n * 4 + 5), Text = $"requeue scheduled — {s.Attempt}", Kind = TranscriptLineKind.Waiting });
        }

        return lines;
    }

    private static readonly string[] ToolLines =
    [
        "tool-call  Read src/Landbridge.Core/SessionState.cs",
        "tool-call  Grep \"MessageState\" (9 hits)",
        "tool-call  Bash dotnet build -c Release",
        "tool-call  Edit ForwardRegistry.cs +12 −3",
        "tool-call  Write docs/RUNBOOK.md",
        "tool-call  Bash git diff --stat",
    ];
    private static readonly string[] NarrationLines =
    [
        "Looks like the retry budget is shared across attempts,",
        "so the fourth pass ought to back off instead of racing.",
        "The relay splice keeps the forward alive past the lease,",
        "which is the behavior the ask was actually about.",
    ];

    public static TranscriptLine RandomTranscriptLine(Random rng)
    {
        var now = DateTime.UtcNow.ToString("HH:mm:ss");
        return rng.NextDouble() < 0.7
            ? new TranscriptLine { Time = now, Text = ToolLines[rng.Next(ToolLines.Length)], Kind = TranscriptLineKind.ToolCall }
            : new TranscriptLine { Time = now, Text = NarrationLines[rng.Next(NarrationLines.Length)], Kind = TranscriptLineKind.Narration };
    }

    private static readonly string[] PermissionAsks =
    [
        "Bash — dotnet test Landbridge.Mcp.Tests",
        "Bash — curl -sf https://relay.internal/health",
        "Bash — psql -c 'select 1' shared-pg",
        "Bash — kill -9 $(pgrep stray-runner)",
    ];

    public static string RandomPermissionAsk(Random rng) => PermissionAsks[rng.Next(PermissionAsks.Length)];
}
