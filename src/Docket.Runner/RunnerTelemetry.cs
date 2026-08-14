using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Docket.Contracts;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Docket.Runner;

/// <summary>
/// docketd's tracing (§1 end-to-end observability). The <c>handle</c> span opened
/// here for an inbound command is parented on the plane's dispatch span via the
/// wire traceparent, so docketd sits inside the one trace that runs create_task →
/// dispatch → runner → worker. A spawned worker inherits this span through its
/// environment (<c>DOCKET_TRACEPARENT</c>, see <see cref="ProcessSupervisor"/>).
///
/// OTLP export is stood up only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set —
/// the Aspire dev loop sets it, a standalone docketd leaves it unset and simply
/// doesn't export, so the runner still runs anywhere without a collector.
/// </summary>
public static class RunnerTelemetry
{
    public const string ActivitySourceName = "Docket.Runner";

    /// <summary>The source the daemon's handle spans come from; registered with the
    /// TracerProvider in <see cref="TryStartOtelExport"/>.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// Stands up trace + metric OTLP export for docketd, or returns null when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is unset. Dispose the result on shutdown
    /// to force-flush both providers before the process exits.
    /// </summary>
    // docketd's OTLP export runs only in the JIT-hosted Aspire dev loop; it is
    // never part of the AOT-published runner (the trace-carrying wire + spans are
    // AOT-clean System.Diagnostics). Suppress the SDK/exporter trim + AOT analysis
    // for this one dev-loop-only method so the rest of the runner stays AOT-clean.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "docketd OTLP export is dev-loop-only; never AOT-published.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "docketd OTLP export is dev-loop-only; never AOT-published.")]
    public static IDisposable? TryStartOtelExport()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
            return null;

        var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(ActivitySourceName)
            .ConfigureResource(r => r.AddService("docketd"))
            .AddHttpClientInstrumentation()
            .AddOtlpExporter()
            .Build();

        var meter = Sdk.CreateMeterProviderBuilder()
            .ConfigureResource(r => r.AddService("docketd"))
            .AddRuntimeInstrumentation()
            .AddOtlpExporter()
            .Build();

        return new OtelProviders(tracer, meter);
    }

    /// <summary>
    /// Opens the <c>handle</c> span for an inbound command, parented on the plane's
    /// dispatch span through <paramref name="traceparent"/> (the wire envelope's
    /// W3C id, §10). Held open while the daemon handles the command so a spawned
    /// worker inherits it via <c>Activity.Current</c>. Null when nothing listens —
    /// handling proceeds unchanged.
    /// </summary>
    public static Activity? StartHandleActivity(RunnerCommand command, string? traceparent)
    {
        var name = $"handle {WireType(command)} {TaskOf(command)}";
        return traceparent is not null && ActivityContext.TryParse(traceparent, null, out var parent)
            ? ActivitySource.StartActivity(name, ActivityKind.Consumer, parent)
            : ActivitySource.StartActivity(name, ActivityKind.Consumer);
    }

    private static string WireType(RunnerCommand command) => command switch
    {
        DispatchCommand => RunnerWire.Dispatch,
        StopCommand => RunnerWire.Stop,
        KillCommand => RunnerWire.Kill,
        OpenForwardCommand => RunnerWire.OpenForward,
        CloseForwardCommand => RunnerWire.CloseForward,
        _ => "command",
    };

    private static string TaskOf(RunnerCommand command) => command switch
    {
        DispatchCommand d => d.Task.ToString(),
        StopCommand s => s.Task.ToString(),
        KillCommand k => k.Task.ToString(),
        OpenForwardCommand o => o.Task.ToString(),
        CloseForwardCommand c => c.Task.ToString(),
        _ => "",
    };

    private sealed class OtelProviders(TracerProvider tracer, MeterProvider meter) : IDisposable
    {
        public void Dispose()
        {
            tracer.ForceFlush(5000);
            tracer.Dispose();
            meter.ForceFlush(5000);
            meter.Dispose();
        }
    }
}
