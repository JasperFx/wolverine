using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Partitioning;
using Wolverine.RabbitMQ;
using Xunit;

namespace SlowTests.Partitioning;

/// <summary>
/// GH-3713. The red baseline for <see cref="webhook_flood_native_ack_chaos" />.
/// </summary>
/// <remarks>
/// <para>An invariant assertion that cannot fail is worth nothing, and "no intra-group concurrency" is
/// exactly the kind of assertion that passes vacuously -- if the flood never overlaps, if the ledger is
/// cleared at the wrong moment, if the handler is never actually reached, the suite goes green while testing
/// nothing. So this class deliberately breaks the guarantee and asserts that the ledger <b>catches</b> it.</para>
///
/// <para><b>How the guarantee is broken.</b> The cluster-wide half of the guarantee is "exactly one consumer
/// per slot", enforced by <c>ExclusiveListenerFamily</c>. Take the message store away and, per GH-4072,
/// <c>WolverineRuntime.startAgentsAsync</c> returns early, no <c>NodeAgentController</c> is built, no
/// exclusive listener is ever assigned, and the durability mode has to be Solo -- where
/// <c>Endpoint.ShouldAutoStartAsListener</c> starts <i>every</i> listener on <i>every</i> node. Three such
/// nodes means three competing consumers on every slot, so two events sharing an entity id can and do run at
/// the same time on different nodes. That is precisely the configuration GH-4072 measured 37 violations on.</para>
///
/// <para>Nothing here is a bug report against Wolverine. It is a statement about the <i>detector</i>: run the
/// same ledger against a topology that genuinely violates the invariant and it must go red. If this test ever
/// starts passing with an empty violation list, the chaos suite next door has stopped proving anything and
/// should not be trusted.</para>
/// </remarks>
[Collection("webhook_flood")]
public class webhook_flood_ledger_red_baseline : IAsyncLifetime
{
    private const string BaseName = "webhookredbaseline";
    private const int SlotCount = 3;

    private readonly List<IHost> _hosts = [];
    private readonly ITestOutputHelper _output;

    public webhook_flood_ledger_red_baseline(ITestOutputHelper output)
    {
        _output = output;
    }

    public ValueTask InitializeAsync()
    {
        NativeAckPartitionedProcessing.Ledger.Clear();

        // Wide enough that competing consumers genuinely overlap rather than merely interleave.
        NativeAckPartitionedProcessing.Dwell = 250.Milliseconds();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.ToArray())
        {
            try
            {
                await host.StopAsync();
                host.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do about a host that will not shut down cleanly during teardown
            }
        }

        _hosts.Clear();
        NativeAckPartitionedProcessing.Ledger.Clear();
        NativeAckPartitionedProcessing.Dwell = 50.Milliseconds();
    }

    /// <summary>
    /// A storeless Solo node. Every slot listener starts here, and on every one of its siblings -- which is
    /// the broken part.
    /// </summary>
    private async Task<IHost> startCompetingHostAsync(string nodeName, bool purgeOnStartup)
    {
        var builder = Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            var rabbit = opts.UseRabbitMq("host=localhost;port=5672").AutoProvision();
            if (purgeOnStartup)
            {
                // Only the first node purges, and only before anything has been published -- purging on a
                // later node would delete this run's own in-flight events.
                rabbit.AutoPurgeOnStartup();
            }

            opts.UseNativeAckLetters(nodeName, topology =>
            {
                topology.ProcessInParallelWithNativeAcks();
                topology.UseShardedRabbitQueues(BaseName, SlotCount);
            });
        });

        var host = await builder.StartAsync();
        _hosts.Add(host);

        return host;
    }

    /// <summary>
    /// Three competing consumers per slot must produce intra-group concurrency, and
    /// <c>AssertNoIntraGroupConcurrency</c> must be the thing that notices.
    /// </summary>
    [Fact]
    public async Task the_ledger_catches_intra_group_concurrency_when_slot_exclusivity_is_removed()
    {
        var first = await startCompetingHostAsync("RedBaseline1", purgeOnStartup: true);
        await startCompetingHostAsync("RedBaseline2", purgeOnStartup: false);
        await startCompetingHostAsync("RedBaseline3", purgeOnStartup: false);

        // Few entities, many events each: the overlap window per entity is what is being provoked, so
        // concentrating traffic on a handful of group ids is the point rather than a shortcut.
        var published = await NativeAckPartitionedProcessing.PumpOutLettersAsync(
            [first.MessageBus], groupCount: 12, messagesPerGroup: 12);

        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(published, 120.Seconds()))
            .ShouldBeTrue("Not every letter was handled, so the run cannot be judged either way");

        var violations = NativeAckPartitionedProcessing.Ledger.Violations;

        _output.WriteLine($"{published.Count} published, "
                          + $"{NativeAckPartitionedProcessing.Ledger.Handled.Count} executions, "
                          + $"{violations.Count} intra-group concurrency violations detected");

        foreach (var violation in violations.Take(10))
        {
            _output.WriteLine("  " + violation);
        }

        violations.ShouldNotBeEmpty(
            "Three competing consumers per slot produced no detected intra-group concurrency. Either the "
            + "cluster somehow serialised anyway, or the ledger has stopped detecting overlap -- in which "
            + "case webhook_flood_native_ack_chaos is passing vacuously and must not be trusted.");

        // And the assertion the chaos suite actually calls has to be the thing that goes red, not merely the
        // raw violation list.
        Should.Throw<Exception>(NativeAckPartitionedProcessing.AssertNoIntraGroupConcurrency);
    }
}
