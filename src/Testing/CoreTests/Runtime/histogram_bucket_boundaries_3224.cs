using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

// GH-3224: configurable, millisecond-tuned histogram bucket boundaries for wolverine-execution-time and
// wolverine-effective-time, applied via instrument advice.
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
        var serviceName = "buckets-" + Guid.NewGuid().ToString("N");

        // Distinctive custom boundaries that won't match the OTel defaults.
        var boundaries = new double[] { 7, 42, 99, 654 };

        var exporter = new CapturingMetricExporter();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("Wolverine:" + serviceName)
            .AddReader(new BaseExportingMetricReader(exporter))
            .Build();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                opts.Metrics.HistogramBucketBoundaries = boundaries;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<MetricSuccessHandler>();
            }).StartAsync();

        // Drive a message so both histograms (execution-time + effective-time) record.
        await host.TrackActivity().SendMessageAndWaitAsync(new MetricSuccess());

        meterProvider!.ForceFlush(5000);

        exporter.BoundsFor(MetricsConstants.ExecutionTime).ShouldBe(boundaries);
        exporter.BoundsFor(MetricsConstants.EffectiveMessageTime).ShouldBe(boundaries);
    }

    private sealed class CapturingMetricExporter : BaseExporter<Metric>
    {
        private readonly ConcurrentDictionary<string, double[]> _bounds = new();

        public double[] BoundsFor(string instrumentName) =>
            _bounds.TryGetValue(instrumentName, out var b) ? b : Array.Empty<double>();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MetricType != MetricType.Histogram)
                {
                    continue;
                }

                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var explicitBounds = new List<double>();
                    foreach (var bucket in point.GetHistogramBuckets())
                    {
                        if (!double.IsPositiveInfinity(bucket.ExplicitBound))
                        {
                            explicitBounds.Add(bucket.ExplicitBound);
                        }
                    }

                    _bounds[metric.Name] = explicitBounds.ToArray();
                }
            }

            return ExportResult.Success;
        }
    }
}
