using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Runtime;

// GH-5012: the runtime's Meter sits in a process-wide static registry until it is disposed, and its
// observable gauges hold callbacks into the runtime, so a disposed host that leaves it behind stays
// reachable for the life of the process.
public class disposing_the_runtime_disposes_its_meter_5012
{
    [Fact]
    public async Task disposing_the_host_disposes_the_wolverine_meter()
    {
        var serviceName = "meter-disposal-" + Guid.NewGuid().ToString("N");
        var meterName = "Wolverine:" + serviceName;

        var published = new List<Instrument>();
        var completed = new List<Instrument>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name != meterName) return;

                lock (published) published.Add(instrument);
                l.EnableMeasurementEvents(instrument);
            },

            // Only fires for an enabled instrument when its Meter is disposed (or the listener stops)
            MeasurementsCompleted = (instrument, _) =>
            {
                if (instrument.Meter.Name != meterName) return;

                lock (completed) completed.Add(instrument);
            }
        };

        listener.Start();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        published.ShouldNotBeEmpty();
        completed.Count.ShouldBe(published.Count);
    }
}
