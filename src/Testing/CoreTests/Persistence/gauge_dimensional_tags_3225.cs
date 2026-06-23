using System.Diagnostics.Metrics;
using CoreTests.Runtime;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence;
using Xunit;

namespace CoreTests.Persistence;

// GH-3225: the wolverine-inbox-count / -outbox-count / -scheduled-count observable gauges must carry dimensional
// tags (source = service name, and database where applicable) so queue depth can be sliced per service/database
// from an external TSDB. Previously the multi-database path baked the database name into the instrument *name*
// (e.g. "wolverine-inbox-count.<db>"), which a TSDB can't group cleanly; that is now a `database` tag instead.
public class gauge_dimensional_tags_3225
{
    private const string TheServiceName = "depth-svc";

    private List<(string Name, Dictionary<string, object?> Tags, int Value)> captureGauges(string? databaseName)
    {
        var runtime = new MockWolverineRuntime();
        runtime.Options.ServiceName = TheServiceName;

        var metrics = new PersistenceMetrics(runtime, runtime.Options.Durability, databaseName);
        metrics.Counts = new PersistedCounts { Incoming = 5, Outgoing = 3, Scheduled = 2 };

        var captured = new List<(string, Dictionary<string, object?>, int)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                // Only this runtime's meter; reference equality keeps the capture isolated from any other test's meter.
                if (ReferenceEquals(instrument.Meter, runtime.Meter))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<int>((inst, measurement, tags, _) =>
        {
            // source/database are instrument-level tags (constant for the gauge's lifetime); OTel merges these into
            // every exported point. The raw MeterListener callback only carries per-measurement tags, so read the
            // instrument tags too.
            var dict = new Dictionary<string, object?>();
            if (inst.Tags != null)
            {
                foreach (var tag in inst.Tags)
                {
                    dict[tag.Key] = tag.Value;
                }
            }

            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }

            captured.Add((inst.Name, dict, measurement));
        });

        listener.Start();
        listener.RecordObservableInstruments();

        metrics.Dispose();

        return captured;
    }

    [Fact]
    public void single_database_gauges_carry_source_tag_and_no_database_tag()
    {
        var gauges = captureGauges(null);

        // All three gauges use their canonical names (no per-database suffix) and report the live counts.
        gauges.ShouldContain(g => g.Name == MetricsConstants.InboxCount && g.Value == 5);
        gauges.ShouldContain(g => g.Name == MetricsConstants.OutboxCount && g.Value == 3);
        gauges.ShouldContain(g => g.Name == MetricsConstants.ScheduledCount && g.Value == 2);

        foreach (var gauge in gauges)
        {
            gauge.Tags.ContainsKey(MetricsConstants.SourceKey).ShouldBeTrue($"{gauge.Name} missing source");
            gauge.Tags[MetricsConstants.SourceKey].ShouldBe(TheServiceName);

            // No database known -> no database dimension.
            gauge.Tags.ContainsKey(MetricsConstants.DatabaseKey).ShouldBeFalse($"{gauge.Name} should not carry a database tag");
        }
    }

    [Fact]
    public void multi_database_gauges_carry_source_and_database_tags_on_canonical_names()
    {
        const string theDatabase = "tenant_db";
        var gauges = captureGauges(theDatabase);

        // The database identity is now a tag, NOT an instrument-name suffix: the names stay canonical so a TSDB can
        // group "wolverine-inbox-count" by the `database` dimension across every database.
        gauges.Select(x => x.Name).ShouldBe(
            new[] { MetricsConstants.InboxCount, MetricsConstants.OutboxCount, MetricsConstants.ScheduledCount },
            ignoreOrder: true);

        foreach (var gauge in gauges)
        {
            gauge.Name.ShouldNotContain(theDatabase); // never baked into the instrument name

            gauge.Tags[MetricsConstants.SourceKey].ShouldBe(TheServiceName);
            gauge.Tags[MetricsConstants.DatabaseKey].ShouldBe(theDatabase);
        }
    }
}
