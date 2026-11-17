using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

// GH-3224: configurable, millisecond-tuned histogram bucket boundaries for wolverine.execution.time and
// wolverine.effective.time, applied via instrument advice.
public class histogram_bucket_boundaries_3224
{
    [Fact]
    public void default_boundaries_are_non_empty_and_ascending_milliseconds()
    {
        var boundaries = MetricsOptions.DefaultHistogramBucketBoundaries;
        boundaries.ShouldNotBeEmpty();
        boundaries.ShouldBe(boundaries.OrderBy(x => x).ToArray()); // strictly ascending order
        boundaries[0].ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task histograms_use_the_configured_bucket_boundaries()
    {
        // Distinctive custom boundaries that won't match the OTel defaults.
        var boundaries = new double[] { 7, 42, 99, 654 };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "buckets-" + Guid.NewGuid().ToString("N");
                opts.Metrics.HistogramBucketBoundaries = boundaries;
                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        // GH-5128: every runtime publishes on a meter named "Wolverine", and an OpenTelemetry MeterProvider
        // folds same-named instruments from every live host in the process into one stream (keeping the
        // first one's advice), so a parallel test's host could decide what an exporter sees. Read the advice
        // off THIS runtime's own instruments instead, which is what OpenTelemetry applies.
        var meter = host.Services.GetRequiredService<IWolverineRuntime>().Meter;
        var advice = new ConcurrentDictionary<string, double[]>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument is Histogram<double> histogram)
                {
                    advice[instrument.Name] = histogram.Advice?.HistogramBucketBoundaries?.ToArray() ?? [];
                }
            }
        };

        listener.Start();

        advice[MetricsConstants.ExecutionTime].ShouldBe(boundaries);
        advice[MetricsConstants.EffectiveMessageTime].ShouldBe(boundaries);
    }
}
