namespace Docket.Mcp.Skills;

/// <summary>
/// The skill bundle (spec §14): Docket's baseline guidance, embedded into the
/// assembly at build time from <c>ideas/skills</c> and served verbatim as MCP
/// resources (§10). This is where domain knowledge lives — the schema stays
/// neutral, the skill is opinionated. The content is authored elsewhere and
/// shipped as-is; nothing here rewrites it.
///
/// URIs are stable and self-describing so the dispatch prompt (and the
/// <c>/docket-*</c> prompts) can point each agent at its own skill without this
/// code and the prompt drifting apart: a worker reads <see cref="WorkerUri"/>,
/// a Lead reads <see cref="LeadUri"/>.
/// </summary>
public static class SkillBundle
{
    /// <summary>Every skill ships as Markdown.</summary>
    public const string Markdown = "text/markdown";

    public const string WorkerUri = "docket://skills/worker";
    public const string LeadUri = "docket://skills/lead";
    public const string EnrollUri = "docket://skills/enroll";
    public const string RunnerConfigUri = "docket://skills/references/runner-config";

    /// <summary>The dispatched-worker skill (<c>docket-worker</c>).</summary>
    public static string Worker { get; } = Load("Docket.Mcp.Skills.worker-skill.md");

    /// <summary>The Lead skill (<c>docket-lead</c>).</summary>
    public static string Lead { get; } = Load("Docket.Mcp.Skills.lead-skill.md");

    /// <summary>The machine-enrollment skill (<c>docket-enroll</c>).</summary>
    public static string Enroll { get; } = Load("Docket.Mcp.Skills.enroll-skill.md");

    /// <summary>The runner-config reference the enroll skill and worker MCP config point at.</summary>
    public static string RunnerConfig { get; } = Load("Docket.Mcp.Skills.references.runner-config.md");

    private static string Load(string logicalName)
    {
        var assembly = typeof(SkillBundle).Assembly;
        using var reader = new StreamReader(
            assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"skill bundle: embedded resource '{logicalName}' is missing — check the " +
                "EmbeddedResource LogicalName in Docket.Mcp.csproj matches this loader."));
        return reader.ReadToEnd();
    }
}
