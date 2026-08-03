using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10 telemetry ingest: the resolution rules, against an injected view of docketd's
/// own environment so no fact depends on the ambient one.
///
/// <para>What these pin is <b>visibility</b>: the harness exports its own token/cost
/// numbers to the operator's collector, stamped with the task that caused them. There
/// is nothing here to enforce — no ceiling, no accounting — and the control plane
/// ingests none of it.</para>
/// </summary>
public sealed class HarnessTelemetryTests
{
    private const string Task = "0123456789abcdef0123456789abcdef";
    private const string Machine = "machine-42";

    /// <summary>An environment that has nothing OTel in it — a standalone docketd.</summary>
    private static Func<string, string?> NoInheritance => _ => null;

    private static Func<string, string?> Inherits(params (string Key, string Value)[] pairs) =>
        key => pairs.FirstOrDefault(p => p.Key == key).Value;

    [Fact]
    public void Otel_off_sets_nothing_even_with_an_endpoint_configured()
    {
        // Off is the default and it means OFF: it is the operator's data going to the
        // operator's collector, so a profile opts in explicitly or not at all.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: false, Endpoint: "http://127.0.0.1:4318"),
            Task, Machine, NoInheritance, out var requestedWithoutEndpoint);

        Assert.Empty(env);
        Assert.False(requestedWithoutEndpoint);
    }

    [Fact]
    public void Otel_on_with_no_destination_sets_nothing_and_says_so()
    {
        // Enabling an exporter that points nowhere buys retry loops against a dead
        // socket, not visibility — so nothing is set, and the caller gets a flag to
        // warn on rather than a silent no-op.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: null),
            Task, Machine, NoInheritance, out var requestedWithoutEndpoint);

        Assert.Empty(env);
        Assert.True(requestedWithoutEndpoint);
    }

    [Fact]
    public void A_configured_endpoint_turns_the_harness_exporter_on_and_carries_the_task_id()
    {
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: "http://127.0.0.1:4318"),
            Task, Machine, NoInheritance, out var requestedWithoutEndpoint);

        Assert.False(requestedWithoutEndpoint);
        Assert.Equal("http://127.0.0.1:4318", env["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal("otlp", env["OTEL_METRICS_EXPORTER"]);
        Assert.Equal("otlp", env["OTEL_LOGS_EXPORTER"]);
        Assert.Equal("http/protobuf", env["OTEL_EXPORTER_OTLP_PROTOCOL"]);

        // §10: "Token attribution must carry a task id." This is that.
        Assert.Equal($"docket.task_id={Task},docket.machine_id={Machine}", env["OTEL_RESOURCE_ATTRIBUTES"]);
    }

    [Fact]
    public void Docketd_sets_no_harness_specific_variable_of_its_own()
    {
        // §10: docketd contains no harness knowledge. Every variable it sets is a
        // vendor-neutral OTel SDK one; a harness's own opt-in flag arrives as config
        // data (telemetry.env), never from a constant in here.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: "http://127.0.0.1:4318"),
            Task, Machine, NoInheritance, out _);

        Assert.All(env.Keys, key => Assert.StartsWith("OTEL_", key, StringComparison.Ordinal));
    }

    [Fact]
    public void Telemetry_env_carries_the_harness_opt_in_as_data()
    {
        // The Claude Code recipe: its enable flag is data in the profile, so supporting
        // another harness stays a config edit (§10).
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: "http://127.0.0.1:4318",
                Env: new Dictionary<string, string> { ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1" }),
            Task, Machine, NoInheritance, out _);

        Assert.Equal("1", env["CLAUDE_CODE_ENABLE_TELEMETRY"]);
    }

    [Fact]
    public void Telemetry_env_is_applied_only_when_telemetry_is_on()
    {
        // The enable flag is not a way around the opt-in: no destination, nothing set —
        // including the harness's own variables.
        var withOtelOff = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: false, Endpoint: "http://127.0.0.1:4318",
                Env: new Dictionary<string, string> { ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1" }),
            Task, Machine, NoInheritance, out _);
        var withNoEndpoint = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: null,
                Env: new Dictionary<string, string> { ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1" }),
            Task, Machine, NoInheritance, out _);

        Assert.Empty(withOtelOff);
        Assert.Empty(withNoEndpoint);
    }

    [Fact]
    public void An_inherited_endpoint_is_a_destination_on_its_own()
    {
        // The Aspire dev loop: docketd already exports to the dashboard's OTLP
        // endpoint, so a profile only has to opt in — no endpoint to repeat.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: null),
            Task, Machine,
            Inherits(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:19015")),
            out var requestedWithoutEndpoint);

        Assert.False(requestedWithoutEndpoint);
        Assert.Equal("http://localhost:19015", env["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Contains($"docket.task_id={Task}", env["OTEL_RESOURCE_ATTRIBUTES"]);
    }

    [Fact]
    public void A_configured_endpoint_beats_the_inherited_one()
    {
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: "http://collector.internal:4318"),
            Task, Machine,
            Inherits(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:19015")),
            out _);

        Assert.Equal("http://collector.internal:4318", env["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    [Fact]
    public void An_inherited_protocol_is_left_alone()
    {
        // An operator who set the protocol on docketd meant it for the machine, and
        // inheritance already delivers it to the child — so we must not overwrite it
        // with a guess. This is the Aspire case: a gRPC dashboard endpoint on a port
        // that looks like nothing in particular.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: null),
            Task, Machine,
            Inherits(
                ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:19015"),
                ("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc")),
            out _);

        Assert.False(env.ContainsKey("OTEL_EXPORTER_OTLP_PROTOCOL"));
    }

    [Theory]
    // The OTel spec's own port assignment, used only as the last-resort default
    // because Claude Code has no protocol default at all and would export nothing.
    [InlineData("http://127.0.0.1:4317", "grpc")]
    [InlineData("http://127.0.0.1:4318", "http/protobuf")]
    [InlineData("https://collector.example.com", "http/protobuf")]
    public void A_protocol_is_always_resolved_when_nothing_upstream_names_one(string endpoint, string expected)
    {
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: endpoint),
            Task, Machine, NoInheritance, out _);

        Assert.Equal(expected, env["OTEL_EXPORTER_OTLP_PROTOCOL"]);
    }

    [Fact]
    public void Inherited_resource_attributes_are_appended_to_never_clobbered()
    {
        // An operator's own dimensions — a cost centre, an environment tag — are how
        // this data gets grouped on their side. Docket's attribution joins them.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: "http://127.0.0.1:4318"),
            Task, Machine,
            Inherits(("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment=prod,cost_center=eng-123")),
            out _);

        Assert.Equal(
            $"deployment.environment=prod,cost_center=eng-123,docket.task_id={Task},docket.machine_id={Machine}",
            env["OTEL_RESOURCE_ATTRIBUTES"]);
    }

    [Fact]
    public void Resource_attributes_from_config_are_appended_to_as_well()
    {
        // telemetry.env can override any default above, but not attribution: a
        // profile-set OTEL_RESOURCE_ATTRIBUTES becomes the base, not the whole value.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: "http://127.0.0.1:4318",
                Env: new Dictionary<string, string> { ["OTEL_RESOURCE_ATTRIBUTES"] = "team.id=platform" }),
            Task, Machine, NoInheritance, out _);

        Assert.Equal(
            $"team.id=platform,docket.task_id={Task},docket.machine_id={Machine}",
            env["OTEL_RESOURCE_ATTRIBUTES"]);
    }

    [Fact]
    public void Telemetry_env_can_override_the_defaults_it_needs_to()
    {
        // Everything except attribution is the operator's call: a collector that only
        // speaks gRPC, or a run that wants events but not metrics.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: "http://127.0.0.1:4318",
                Env: new Dictionary<string, string>
                {
                    ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
                    ["OTEL_LOGS_EXPORTER"] = "none",
                }),
            Task, Machine, NoInheritance, out _);

        Assert.Equal("grpc", env["OTEL_EXPORTER_OTLP_PROTOCOL"]);
        Assert.Equal("none", env["OTEL_LOGS_EXPORTER"]);
        Assert.Equal("otlp", env["OTEL_METRICS_EXPORTER"]);
        Assert.Contains($"docket.task_id={Task}", env["OTEL_RESOURCE_ATTRIBUTES"]);
    }

    [Fact]
    public void Telemetry_env_cannot_reach_the_variables_docketd_owns()
    {
        // §10 fixes DOCKET_MACHINE_ID/DOCKET_TASK_ID on every spawn "not configurably":
        // stray-process cleanup scans for them, so a profile that could overwrite one
        // would break restart-equals-reboot, not just mislabel a metric.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: "http://127.0.0.1:4318",
                Env: new Dictionary<string, string>
                {
                    ["DOCKET_TASK_ID"] = "not-this-task",
                    ["DOCKET_MACHINE_ID"] = "not-this-machine",
                    ["DOCKET_WORKER_TOKEN"] = "forged",
                    ["DOCKET_TRACEPARENT"] = "forged",
                }),
            Task, Machine, NoInheritance, out _);

        Assert.All(env.Keys, key => Assert.DoesNotContain("DOCKET_", key, StringComparison.Ordinal));
    }

    [Fact]
    public void An_operator_chosen_machine_id_cannot_forge_an_attribute()
    {
        // `,` and `=` are structural in this value; a machine id is a free string an
        // operator passes with --machine-id, so it is sanitized rather than trusted.
        var env = HarnessTelemetry.SpawnEnvironment(
            new TelemetryConfig(Otel: true, Endpoint: "http://127.0.0.1:4318"),
            Task, "shared box,user.email=someone@example.com", NoInheritance, out _);

        Assert.Equal(
            $"docket.task_id={Task},docket.machine_id=shared_box_user.email_someone_example.com",
            env["OTEL_RESOURCE_ATTRIBUTES"]);
    }
}

/// <summary>
/// The same §10 rules, but observed on a REAL spawned child: the harness's
/// <c>echo-env</c> mode writes the environment it was actually handed, which is the
/// only way to prove docketd's resolution survives
/// <see cref="System.Diagnostics.ProcessStartInfo"/> and reaches a worker.
/// </summary>
public sealed class HarnessTelemetrySpawnTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

    [Fact]
    public async Task An_opted_in_profile_hands_its_worker_a_configured_exporter_and_the_task_id()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("echo-env", telemetry: new TelemetryConfig(
                Otel: true,
                Endpoint: "http://127.0.0.1:4318",
                Env: new Dictionary<string, string>
                {
                    ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1",
                    // A second, deliberately unmistakable variable: the ambient
                    // environment may well already carry the Claude Code flag (a
                    // developer running docketd from a harness that sets its own), so
                    // this one proves telemetry.env reached the child from the PROFILE.
                    ["HARNESS_TELEMETRY_TEST_MARKER"] = "from-profile",
                })),
            "machine-42");

        var env = await ReadEnvMarker(task);

        Assert.Equal("1", env["CLAUDE_CODE_ENABLE_TELEMETRY"]);
        Assert.Equal("from-profile", env["HARNESS_TELEMETRY_TEST_MARKER"]);
        Assert.Equal("http://127.0.0.1:4318", env["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal("http/protobuf", env["OTEL_EXPORTER_OTLP_PROTOCOL"]);
        Assert.Equal("otlp", env["OTEL_METRICS_EXPORTER"]);
        Assert.Equal("otlp", env["OTEL_LOGS_EXPORTER"]);

        // Every metric and event the harness emits will carry this, which is what lets
        // a collector bucket token/cost per Docket task (§10 attribution).
        Assert.Contains($"docket.task_id={task}", env["OTEL_RESOURCE_ATTRIBUTES"]);
        Assert.Contains("docket.machine_id=machine-42", env["OTEL_RESOURCE_ATTRIBUTES"]);

        // The task id is on the spawn twice for two different consumers: DOCKET_TASK_ID
        // for hooks and stray cleanup (§10), the resource attribute for the collector.
        Assert.Equal(task.ToString(), env["DOCKET_TASK_ID"]);

        Assert.True(supervisor.Kill(task));
    }

    [Fact]
    public async Task A_profile_that_did_not_opt_in_gets_no_telemetry_variables()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // The default profile: telemetry.otel unset. Whatever the ambient environment
        // holds still flows through by inheritance — that is deliberate, and how
        // docketd's own exporter is configured — but docketd itself adds nothing.
        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("echo-env"), "machine-42");

        var env = await ReadEnvMarker(task);
        AssertDocketdSetNothing(env, task);

        Assert.True(supervisor.Kill(task));
    }

    [Fact]
    public async Task Telemetry_requested_with_no_destination_leaves_the_worker_untouched()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // otel on, no endpoint configured. Whether one is inherited is the ambient
        // machine's business, so this covers both: with no destination docketd sets
        // nothing at all, and with an inherited one it resolves against it (the
        // documented inheritance path, not a no-op).
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("echo-env", telemetry: new TelemetryConfig(
                Otel: true,
                Endpoint: null,
                Env: new Dictionary<string, string> { ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1" })),
            "machine-42");

        var env = await ReadEnvMarker(task);

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
            AssertDocketdSetNothing(env, task);
        else
            Assert.Contains($"docket.task_id={task}", env["OTEL_RESOURCE_ATTRIBUTES"]);

        Assert.True(supervisor.Kill(task));
    }

    /// <summary>
    /// Proves docketd contributed no telemetry of its own, which is not the same as
    /// the child having none: <c>UseShellExecute=false</c> copies docketd's whole
    /// environment to the worker, so a machine that already has <c>OTEL_*</c> (or a
    /// harness flag) set — a developer running under a harness that sets its own, or a
    /// docketd exporting its traces — passes them down regardless of this profile.
    /// So compare against docketd's own environment rather than asserting absence, and
    /// assert absence only for <c>docket.task_id</c>, which nothing but this code adds.
    /// </summary>
    private static void AssertDocketdSetNothing(Dictionary<string, string> env, TaskId task)
    {
        string[] resolvable =
        [
            "OTEL_EXPORTER_OTLP_ENDPOINT",
            "OTEL_EXPORTER_OTLP_PROTOCOL",
            "OTEL_METRICS_EXPORTER",
            "OTEL_LOGS_EXPORTER",
            "OTEL_RESOURCE_ATTRIBUTES",
            "CLAUDE_CODE_ENABLE_TELEMETRY",
        ];

        foreach (var key in resolvable)
        {
            var ambient = Environment.GetEnvironmentVariable(key);
            env.TryGetValue(key, out var onChild);
            Assert.Equal(ambient, onChild);
        }

        Assert.DoesNotContain("docket.task_id",
            env.TryGetValue("OTEL_RESOURCE_ATTRIBUTES", out var attrs) ? attrs : "");
        Assert.DoesNotContain(task.ToString(),
            env.TryGetValue("OTEL_RESOURCE_ATTRIBUTES", out var same) ? same : "");
    }

    /// <summary>Reads the <c>echo-env</c> marker into a map of the child's actual environment.</summary>
    private async Task<Dictionary<string, string>> ReadEnvMarker(TaskId task)
    {
        var path = Path.Combine(_workRoot, task.ToString(), "env");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(15)),
            "harness never recorded its environment");

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            var split = line.IndexOf('=', StringComparison.Ordinal);
            if (split > 0)
                env[line[..split]] = line[(split + 1)..];
        }
        return env;
    }

    public void Dispose()
    {
        try { _supervisor?.KillAll(); } catch { /* best effort */ }
        TestKit.TryDeleteRoot(_workRoot);
    }
}
