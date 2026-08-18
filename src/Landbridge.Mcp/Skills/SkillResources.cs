using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Landbridge.Mcp.Skills;

/// <summary>
/// Serves the skill bundle (spec §14) as MCP resources so it reaches every agent
/// on connect (§10). Each method is a "direct resource" — a fixed <c>landbridge://</c>
/// URI with no template parameters — returning the embedded Markdown verbatim; the
/// SDK wraps the returned string in a <c>TextResourceContents</c>.
///
/// <para>
/// Scoping is <b>advisory</b>, by deliberate choice. All four skills list and read
/// for any authenticated principal, and an agent finds its own URI through
/// <c>resources/list</c> — no MCP prompt is registered on this branch, so the
/// <c>/landbridge-*</c> prompts §10 sketches are not what steers anyone here yet. Three
/// reasons this is not a hard per-principal ACL:
/// </para>
/// <list type="number">
///   <item><description>
///     Spec §3: "Skills and prompts are advisory and can be bypassed" — guidance,
///     not a guarantee. The guarantees live in the schema and the token principal,
///     which the tool surface already enforces; skills are read-only prose.
///   </description></item>
///   <item><description>
///     The SDK's attribute resource model exposes a single session-wide resource
///     list (<c>resources/list</c>) with no per-request filter hook — the same shape
///     as the tool surface, where <c>list_tools</c> returns every tool to every
///     principal and authority is checked when a tool is <i>called</i>. Read-time
///     gating (inject <c>IHttpContextAccessor</c>, as the tools do) is available and
///     could be layered on later, but is deliberately not applied here.
///   </description></item>
///   <item><description>
///     The audiences don't map cleanly onto one principal: <see cref="Enroll"/> and
///     <see cref="RunnerConfig"/> are operator/machine-facing and may legitimately be
///     read by a Human, a Machine, or during the anonymous enrollment exchange (§5),
///     so a hard ACL would risk locking out a legitimate reader.
///   </description></item>
/// </list>
/// Skills are not secret, so the cost of a worker being able to read the Lead skill
/// is zero; the value of scoping would be keeping an agent pointed at the right
/// guidance, and the URI names plus the descriptions below are all that does that
/// today.
/// </summary>
[McpServerResourceType]
public sealed class SkillResources
{
    [McpServerResource(
        Name = "landbridge-worker",
        Title = "Landbridge worker skill",
        UriTemplate = SkillBundle.WorkerUri,
        MimeType = SkillBundle.Markdown)]
    [Description("How to execute a Landbridge session as a worker: isolating yourself on a shared " +
                 "machine, persisting before asking, registering services, reporting a result, " +
                 "and raising blockers instead of guessing. Read this first when dispatched.")]
    public static string Worker() => SkillBundle.Worker;

    [McpServerResource(
        Name = "landbridge-lead",
        Title = "Landbridge lead skill",
        UriTemplate = SkillBundle.LeadUri,
        MimeType = SkillBundle.Markdown)]
    [Description("How to lead a Landbridge Team: claiming and reattaching, decomposing work into " +
                 "sessions, choosing completion modes, answering worker questions, and cancelling " +
                 "or closing work.")]
    public static string Lead() => SkillBundle.Lead;

    [McpServerResource(
        Name = "landbridge-enroll",
        Title = "Landbridge enrollment skill",
        UriTemplate = SkillBundle.EnrollUri,
        MimeType = SkillBundle.Markdown)]
    [Description("How to enroll a machine into a Landbridge Machine Group: probing the local harness, " +
                 "writing the landbridged runner config, registering the daemon as a service, and " +
                 "smoke-testing the machine before real work reaches it.")]
    public static string Enroll() => SkillBundle.Enroll;

    [McpServerResource(
        Name = "landbridge-runner-config",
        Title = "landbridged runner config reference",
        UriTemplate = SkillBundle.RunnerConfigUri,
        MimeType = SkillBundle.Markdown)]
    [Description("Reference for the landbridged runner config: the full schema, spawn-argv " +
                 "substitutions, the generated worker MCP config, and a worked Claude Code profile. " +
                 "Referenced by the enroll skill and describes the channel a worker dials the plane with.")]
    public static string RunnerConfig() => SkillBundle.RunnerConfig;
}
