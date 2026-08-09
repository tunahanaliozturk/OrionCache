namespace Moongazing.OrionCache.Diagnostics;

using System.Diagnostics.Metrics;

using Moongazing.Orion.Abstractions.Diagnostics;

/// <summary>
/// OpenTelemetry instrumentation for the cache. Built on the Orion family's
/// <see cref="OrionInstrumentation"/> spine: a <see cref="Meter"/> named <c>Moongazing.OrionCache</c>
/// (subscribe by that name) carrying <c>orion.cache.hits</c>, <c>orion.cache.misses</c>,
/// <c>orion.cache.factory_runs</c> (values actually produced), and <c>orion.cache.stampede_waits</c>
/// (callers that waited on another's single-flight and were served its result). The recording methods
/// only touch counters, so instrumentation never throws into the cache path. Multi-tenant /
/// multi-region labels configured through <see cref="OrionInstrumentation.SetStaticTags"/> are stamped
/// onto every measurement.
/// <para>A process-wide <see cref="Shared"/> instance makes telemetry emit by default.</para>
/// </summary>
public sealed class CacheDiagnostics : OrionInstrumentation
{
    /// <summary>The meter name OpenTelemetry consumers subscribe to.</summary>
    public const string MeterName = "Moongazing.OrionCache";

    private static readonly System.Lazy<CacheDiagnostics> SharedInstance =
        new(static () => new CacheDiagnostics());

    /// <summary>Create the meter and its instruments.</summary>
    public CacheDiagnostics()
        : base(OrionTelemetry.ScopeName("OrionCache"), MeterVersion.Value)
    {
        Hits = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("cache", "hits"),
            unit: "{lookup}",
            description: "Lookups served from the cache.");

        Misses = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("cache", "misses"),
            unit: "{lookup}",
            description: "Lookups not present (or expired) in the cache.");

        FactoryRuns = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("cache", "factory_runs"),
            unit: "{run}",
            description: "Value-factory invocations (a cold value actually produced).");

        StampedeWaits = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("cache", "stampede_waits"),
            unit: "{caller}",
            description: "Callers that waited on another caller's single-flight and were served its result.");
    }

    /// <summary>The process-wide default instance, so telemetry emits without explicit wiring.</summary>
    public static CacheDiagnostics Shared => SharedInstance.Value;

    /// <summary>Counts lookups served from the cache.</summary>
    public Counter<long> Hits { get; }

    /// <summary>Counts lookups not present or expired.</summary>
    public Counter<long> Misses { get; }

    /// <summary>Counts value-factory invocations.</summary>
    public Counter<long> FactoryRuns { get; }

    /// <summary>Counts callers served by another's single-flight.</summary>
    public Counter<long> StampedeWaits { get; }

    /// <summary>Record a cache hit.</summary>
    public void RecordHit() => Hits.Add(1, StaticTags);

    /// <summary>Record a cache miss.</summary>
    public void RecordMiss() => Misses.Add(1, StaticTags);

    /// <summary>Record a value-factory run.</summary>
    public void RecordFactoryRun() => FactoryRuns.Add(1, StaticTags);

    /// <summary>Record a caller served by another's single-flight.</summary>
    public void RecordStampedeWait() => StampedeWaits.Add(1, StaticTags);
}
