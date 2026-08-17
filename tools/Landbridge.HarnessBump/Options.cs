namespace Landbridge.HarnessBump;

/// <summary>Everything the run can be pointed at, so the tool is testable and dry-runnable.</summary>
public sealed record Options
{
    public string RepoRoot { get; init; } = Directory.GetCurrentDirectory();
    public string Repository { get; init; } =
        Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") is { Length: > 0 } r ? r : "vezaynk/landbridge-mcp";
    public string Workflow { get; init; } = "ci.yml";
    public string CiPath { get; init; } = ".github/workflows/ci.yml";
    public string BaseBranch { get; init; } = "master";

    /// <summary>Report the plan and stop — no branch, no PR, no dispatch, nothing spent.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Permits the mutating half (push, PR, dispatch, merge) outside GitHub Actions.
    /// </summary>
    /// <remarks>
    /// Off by default because the obvious accident is expensive: someone debugging the bot runs it
    /// locally, forgets <c>--dry-run</c>, and pushes a branch and burns a real-harness dispatch.
    /// The read-only half needs no flag, so exploring what the bot would do stays frictionless.
    /// </remarks>
    public bool AllowLocalWrites { get; init; }

    /// <summary>True in GitHub Actions, where mutating IS the job.</summary>
    public static bool InActions =>
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is "true";

    /// <summary>Whether this process may create a branch, a PR, a dispatch or a merge.</summary>
    public static bool MutationsAllowed(bool dryRun, bool inActions, bool allowLocalWrites) =>
        !dryRun && (inActions || allowLocalWrites);

    /// <summary>
    /// Model slugs to pass as dispatch inputs. Empty by default ON PURPOSE: ci.yml's own
    /// <c>workflow_dispatch</c> defaults are then used, which keeps ci.yml the single source of
    /// truth for the current slug. Duplicating the defaults here would mean a server-side slug
    /// retirement had to be fixed in two files.
    /// </summary>
    public string? CodexModel { get; init; }
    public string? OpenCodeModel { get; init; }

    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(60);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public string CommitterName { get; init; } =
        Environment.GetEnvironmentVariable("LANDBRIDGE_BUMP_COMMITTER_NAME") ?? "landbridge harness-bump bot";
    public string CommitterEmail { get; init; } =
        Environment.GetEnvironmentVariable("LANDBRIDGE_BUMP_COMMITTER_EMAIL") ?? "noreply@github.com";

    public string CiFullPath => Path.Combine(RepoRoot, CiPath);

    /// <summary>Parses argv. Unknown flags are an error rather than a silent no-op.</summary>
    public static Options Parse(string[] args)
    {
        var options = new Options
        {
            // The workflow passes this rather than a flag, so the YAML step stays a bare
            // `dotnet run` with no expression logic in it.
            DryRun = Environment.GetEnvironmentVariable("LANDBRIDGE_BUMP_DRY_RUN") is "1" or "true" or "TRUE",
        };

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    options = options with { DryRun = true };
                    break;
                case "--allow-local-writes":
                    options = options with { AllowLocalWrites = true };
                    break;
                case "--repo":
                    options = options with { Repository = Next(args, ref i) };
                    break;
                case "--repo-root":
                    options = options with { RepoRoot = Next(args, ref i) };
                    break;
                case "--workflow":
                    options = options with { Workflow = Next(args, ref i) };
                    break;
                case "--ci-path":
                    options = options with { CiPath = Next(args, ref i) };
                    break;
                case "--base":
                    options = options with { BaseBranch = Next(args, ref i) };
                    break;
                case "--codex-model":
                    options = options with { CodexModel = Next(args, ref i) };
                    break;
                case "--opencode-model":
                    options = options with { OpenCodeModel = Next(args, ref i) };
                    break;
                case "--run-timeout-minutes":
                    options = options with { RunTimeout = TimeSpan.FromMinutes(double.Parse(Next(args, ref i))) };
                    break;
                default:
                    throw new ArgumentException($"unknown argument `{args[i]}`");
            }
        }
        return options;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"`{args[i]}` needs a value");
        return args[++i];
    }
}
