using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3852. <see cref="StartAgents.StartBatchAsync" /> got bounded parallelism in GH-3604/D3; the stop side
/// never did, so a chunk of stops ran strictly one at a time. At <c>AgentStartBatchSize = 50</c> against a
/// source whose shards are slow to let go, that is the whole chunk's stop cost in series before
/// <see cref="ReassignAgents" /> can cascade a single start — and every agent in the chunk is down for the
/// duration.
///
/// <para>It was survivable only because the leader re-decided the same move every cycle, trickling agents
/// onto the destination alongside the batch. Removing that churn is what exposed the serial stop:
/// <c>agent_reassignment_at_scale</c> went from converging in 176s to 31s once stops fanned out.</para>
/// </summary>
public class stop_agents_batch_parallelism
{
    private static (IWolverineRuntime Runtime, Func<int> PeakConcurrency) runtimeThatBlocksOnStop(
        int parallelism, TimeSpan stopDelay)
    {
        var options = new WolverineOptions();
        options.Durability.MaxAgentStartParallelism = parallelism;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.Logger.Returns(NullLogger.Instance);

        var current = 0;
        var peak = 0;
        var gate = new object();

        var agents = Substitute.For<IAgentRuntime>();
        agents.StopLocallyAsync(Arg.Any<Uri>()).Returns(async _ =>
        {
            lock (gate)
            {
                current++;
                peak = Math.Max(peak, current);
            }

            await Task.Delay(stopDelay);

            lock (gate) current--;
            return;
        });

        runtime.Agents.Returns(agents);

        return (runtime, () => { lock (gate) return peak; });
    }

    [Fact]
    public async Task stops_a_batch_with_bounded_parallelism()
    {
        var (runtime, peak) = runtimeThatBlocksOnStop(10, 200.Milliseconds());

        var uris = Enumerable.Range(0, 30).Select(i => new Uri($"fake://agent-{i}")).ToArray();

        var cascaded = await new StopAgents(uris).ExecuteAsync(runtime, CancellationToken.None);

        // Every agent still reported stopped, and in the same shape as before.
        cascaded.OfType<AgentsStopped>().Single().AgentUris.OrderBy(x => x.ToString())
            .ShouldBe(uris.OrderBy(x => x.ToString()));

        // Serially this would never exceed 1. Bounded, it must not exceed the configured degree either.
        peak().ShouldBeGreaterThan(1);
        peak().ShouldBeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task honours_a_parallelism_of_one()
    {
        var (runtime, peak) = runtimeThatBlocksOnStop(1, 10.Milliseconds());

        var uris = Enumerable.Range(0, 5).Select(i => new Uri($"fake://agent-{i}")).ToArray();

        await new StopAgents(uris).ExecuteAsync(runtime, CancellationToken.None);

        peak().ShouldBe(1);
    }

    /// <summary>
    /// A single failing stop must cost only itself — the batch still reports everything else, exactly as the
    /// serial loop's per-agent try/catch did.
    /// </summary>
    [Fact]
    public async Task one_failing_stop_does_not_lose_the_rest_of_the_batch()
    {
        var options = new WolverineOptions();
        options.Durability.MaxAgentStartParallelism = 5;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.Logger.Returns(NullLogger.Instance);

        var doomed = new Uri("fake://agent-2");

        var agents = Substitute.For<IAgentRuntime>();
        agents.StopLocallyAsync(Arg.Any<Uri>()).Returns(call =>
            call.Arg<Uri>() == doomed
                ? Task.FromException(new InvalidOperationException("nope"))
                : Task.CompletedTask);

        runtime.Agents.Returns(agents);

        var uris = Enumerable.Range(0, 5).Select(i => new Uri($"fake://agent-{i}")).ToArray();

        var cascaded = await new StopAgents(uris).ExecuteAsync(runtime, CancellationToken.None);

        var stopped = cascaded.OfType<AgentsStopped>().Single().AgentUris;
        stopped.Length.ShouldBe(4);
        stopped.ShouldNotContain(doomed);
    }
}
