using CoreTests.Acceptance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4593. Supplying an <see cref="INodeLoadMonitor" /> is not enough on its own — the reading has to
/// survive the round trip through the message store, and a store that drops it turns the whole feature
/// into a silent no-op. Worse than a no-op, in fact: a node that advertises nothing is read by the leader
/// as having unlimited headroom, so every node looks equally idle and placement ignores capacity
/// entirely, which is the opposite of what enabling the flag asked for.
/// </summary>
public class capacity_aware_warns_on_a_store_that_cannot_advertise
{
    private static async Task<RecordingLoggerProvider> startAsync(bool capacityAware)
    {
        var logger = new RecordingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logger))
            .UseWolverine(opts =>
            {
                opts.Durability.CapacityAwareAssignment = capacityAware;

                if (capacityAware)
                {
                    opts.Durability.NodeLoadMonitor = new ConstantLoadMonitor(17);
                }
            }).StartAsync(TestContext.Current.CancellationToken);

        return logger;
    }

    [Fact]
    public async Task warns_when_the_store_does_not_persist_the_reading()
    {
        // No message store at all, so NullMessageStore -- which takes the interface default of "I do not
        // advertise node load". This is the shape every store but PostgreSQL was in before GH-4593, and it
        // said nothing at all.
        var logger = await startAsync(capacityAware: true);

        logger.Warnings.ShouldContain(x => x.Contains("does not persist a node's load reading"));
    }

    [Fact]
    public async Task says_nothing_when_the_feature_is_off()
    {
        var logger = await startAsync(capacityAware: false);

        logger.Warnings.ShouldNotContain(x => x.Contains("does not persist a node's load reading"));
    }

    private class ConstantLoadMonitor(double? load) : INodeLoadMonitor
    {
        public double? CurrentLoad() => load;
    }
}
