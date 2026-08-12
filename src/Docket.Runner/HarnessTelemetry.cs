using System.Globalization;

namespace Docket.Runner;

/// <summary>
/// §10 telemetry ingest: turns a profile's <c>telemetry</c> block into the
/// environment a harness needs to export <b>its own</b> token/cost telemetry to
/// the operator's collector, stamped with the Docket task that caused the spend.
///
/// <para><b>Visibility, not enforcement.</b> Nothing here meters, caps, or
/// accounts for anything, and the control plane ingests none of it. Docket does
/// not sit between the harness and the model provider (§10), so these are the
/// harness's own numbers going to the operator's own collector — best-effort by
/// construction. What docketd contributes is the one thing the harness cannot
/// know on its own: which task the spend belongs to. §10: "Token attribution must
/// carry a task id" — a shared machine's spend is otherwise unattributable to the
/// work that caused it. That id rides <c>OTEL_RESOURCE_ATTRIBUTES</c> as
/// <c>docket.task_id</c>.</para>
///
/// <para><b>No harness knowledge (§10).</b> docketd sets only vendor-neutral
/// OTel SDK variables — the ones any OTel-emitting harness reads. A harness's own
/// opt-in flag is harness-specific, so it is <em>data</em>: whatever
/// <see cref="TelemetryConfig.Env"/> declares is applied on the spawn (Claude
/// Code: <c>CLAUDE_CODE_ENABLE_TELEMETRY=1</c>; see the worked example in
/// <c>runner-config.md</c>). Supporting a new harness stays a config edit, never
/// a change here.</para>
///
/// <para><b>Opt-in per profile, and never without a destination.</b> It is the
/// operator's data going to the operator's collector, so <c>otel: false</c> (the
/// default) sets nothing at all. Neither does <c>otel: true</c> with no endpoint
/// configured and none inherited — enabling an exporter that points nowhere buys
/// a worker retry loops against a dead socket instead of visibility.</para>
/// </summary>
public static class HarnessTelemetry
{
    /// <summary>Where the harness ships OTLP. Set from config, else left as inherited.</summary>
    public const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>OTLP wire protocol. The OTel SDK has a default; Claude Code does not, so one is always resolved.</summary>
    public const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";

    public const string MetricsExporterVar = "OTEL_METRICS_EXPORTER";
    public const string LogsExporterVar = "OTEL_LOGS_EXPORTER";

    /// <summary>Carries <c>docket.task_id</c> — the attribution §10 requires — onto every metric and event.</summary>
    public const string ResourceAttributesVar = "OTEL_RESOURCE_ATTRIBUTES";

    /// <summary>The task-attribution key stamped on everything the harness emits.</summary>
    public const string TaskAttribute = "docket.task_id";

    /// <summary>Which machine's collector-visible spend this is — one Team's tasks can span machines, and vice versa.</summary>
    public const string MachineAttribute = "docket.machine_id";

    /// <summary>
    /// The OTel spec's own port assignment: 4317 is gRPC, 4318 is HTTP. Used only
    /// as the fallback when neither the profile nor docketd's inherited environment
    /// names a protocol, since a harness with no protocol default (Claude Code)
    /// otherwise exports nothing at all.
    /// </summary>
    private const int GrpcDefaultPort = 4317;

    private const string GrpcProtocol = "grpc";
    private const string HttpProtocol = "http/protobuf";

    /// <summary>
    /// Variables docketd owns and <c>telemetry.env</c> therefore cannot set. §10 fixes
    /// <c>DOCKET_MACHINE_ID</c>/<c>DOCKET_TASK_ID</c> on every spawn "not configurably"
    /// — stray-process cleanup scans for them, so a profile that could overwrite one
    /// would break the restart-equals-reboot guarantee rather than just mislabel a
    /// metric. The worker token and traceparent are per-spawn secrets/context for the
    /// same reason.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "DOCKET_MACHINE_ID",
        "DOCKET_TASK_ID",
        "DOCKET_WORKER_TOKEN",
        "DOCKET_TRACEPARENT",
    };

    /// <summary>
    /// The variables to set on a worker spawn, or empty when this profile asks for
    /// no harness telemetry (or asks for it with nowhere to send it — see
    /// <paramref name="requestedWithoutEndpoint"/>). The child inherits the rest of
    /// docketd's environment wholesale, so anything not named here (headers, TLS,
    /// compression, export intervals, the trace beta) flows through untouched and an
    /// operator can set it once on docketd.
    /// </summary>
    /// <param name="inherited">
    /// Reads docketd's own environment — the destination an operator may already
    /// have configured for docketd itself (the Aspire dev loop sets it), and the
    /// values a caller-set variable would override.
    /// </param>
    /// <param name="requestedWithoutEndpoint">
    /// True when the profile asked for telemetry but no destination resolved, so the
    /// caller can say so once rather than silently doing nothing.
    /// </param>
    public static IReadOnlyDictionary<string, string> SpawnEnvironment(
        TelemetryConfig telemetry,
        string taskId,
        string machineId,
        Func<string, string?> inherited,
        out bool requestedWithoutEndpoint)
    {
        requestedWithoutEndpoint = false;
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!telemetry.Otel)
            return empty;

        // Explicit config beats inheritance; absent both there is no destination and
        // we set nothing (the caller warns once).
        var endpoint = Trimmed(telemetry.Endpoint) ?? Trimmed(inherited(EndpointVar));
        if (endpoint is null)
        {
            requestedWithoutEndpoint = true;
            return empty;
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EndpointVar] = endpoint,
            [MetricsExporterVar] = "otlp",
            [LogsExporterVar] = "otlp",
        };

        // A protocol is only resolved when nothing upstream named one: an operator who
        // set it on docketd meant it for the whole machine, and inheritance already
        // delivers it.
        if (Trimmed(inherited(ProtocolVar)) is null)
            env[ProtocolVar] = DefaultProtocolFor(endpoint);

        // Harness-specific opt-ins (§10: data, not docketd knowledge). Applied before
        // the resource attributes so an operator can override any default above —
        // including the protocol and the exporters — while attribution still stands.
        // The variables docketd owns are not on the table (see Reserved).
        foreach (var (key, value) in telemetry.Env)
            if (!string.IsNullOrWhiteSpace(key) && !Reserved.Contains(key))
                env[key] = value;

        // Append, never clobber: an operator's own resource attributes (a team, a cost
        // centre, an environment tag) survive alongside Docket's attribution, whether
        // they arrived by inheritance or from telemetry.env.
        var baseAttributes = Trimmed(env.TryGetValue(ResourceAttributesVar, out var fromConfig) ? fromConfig : null)
            ?? Trimmed(inherited(ResourceAttributesVar));
        env[ResourceAttributesVar] = AppendAttributes(baseAttributes, taskId, machineId);

        return env;
    }

    /// <summary>
    /// Builds the <c>OTEL_RESOURCE_ATTRIBUTES</c> value: whatever was already there,
    /// then Docket's attribution. Values are sanitized because the spec's baggage-ish
    /// encoding gives <c>,</c> and <c>=</c> structural meaning — a machine id an
    /// operator chose freely must not be able to forge or split an attribute.
    /// </summary>
    private static string AppendAttributes(string? existing, string taskId, string machineId)
    {
        var mine = string.Create(CultureInfo.InvariantCulture,
            $"{TaskAttribute}={Sanitize(taskId)},{MachineAttribute}={Sanitize(machineId)}");
        return existing is null ? mine : existing.TrimEnd(',') + "," + mine;
    }

    /// <summary>US-ASCII letters, digits, dot, dash and underscore pass; anything else becomes an underscore.</summary>
    private static string Sanitize(string value)
    {
        if (value.Length == 0)
            return value;
        var safe = value.ToCharArray();
        for (var i = 0; i < safe.Length; i++)
        {
            var c = safe[i];
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_';
            if (!ok)
                safe[i] = '_';
        }
        return new string(safe);
    }

    private static string DefaultProtocolFor(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Port == GrpcDefaultPort
            ? GrpcProtocol
            : HttpProtocol;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
