using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-4167. BatchingOptions.BuildHandler is lazy init on the message-handling path, reached through
/// HandlerPipeline's LightweightCache&lt;Type, IExecutor&gt; whose indexer does not lock — two concurrent
/// misses each invoke the factory and each returns its own instance.
///
/// A duplicate stateless executor is harmless. A duplicate BatchingProcessor is not: each owns a
/// separate BatchingChannel buffer, its own flush Timer, and two Blocks with live worker tasks. Two
/// instances means the batch silently splits, and the loser is never returned to anyone so it is
/// never disposed — its timer and worker tasks leak for the life of the process.
/// </summary>
public class batching_processor_build_race
{
    private static Task<IHost> hostAsync(TimeSpan triggerTime, string queueName)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.BatchMessagesOf<ExpiryItem>(batching =>
                {
                    batching.TriggerTime = triggerTime;
                    batching.LocalExecutionQueueName = queueName;
                });
            }).StartAsync();
    }

    /// <summary>
    /// The direct expression of the race, and the one that fails without the fix regardless of which
    /// JasperFx version is referenced. The end-to-end test below can only fail once Block stops running
    /// continuations inline on the publisher (jasperfx#714), because that inline execution serializes
    /// the first messages onto one thread and closes the window.
    /// </summary>
    [Fact]
    public async Task concurrent_BuildHandler_yields_one_shared_processor()
    {
        using var host = await hostAsync(1.Seconds(), "race_direct");

        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        var options = runtime.Options.BatchDefinitions.Single(x => x.ElementType == typeof(ExpiryItem));

        const int racers = 8;
        var gate = new Barrier(racers);

        var handlers = await Task.WhenAll(Enumerable.Range(0, racers).Select(_ => Task.Run(() =>
        {
            gate.SignalAndWait();
            return options.BuildHandler(runtime);
        })));

        // Reference equality: every racer must have been handed the SAME processor. Without the fix
        // each concurrent miss builds and returns its own, each with its own buffer and flush timer.
        handlers.Distinct().Count().ShouldBe(1,
            $"BuildHandler produced {handlers.Distinct().Count()} distinct BatchingProcessor instances " +
            "across concurrent callers; every extra one owns an orphaned Timer and worker tasks.");
    }

    /// <summary>
    /// End-to-end consequence: the two members of a concurrent first wave must land in one batch.
    /// NOTE: this passes against JasperFx 2.56.0 even without the fix, because Block's inline
    /// continuations serialize the first two messages. It becomes a real guard once jasperfx#714 ships.
    /// </summary>
    [Fact]
    public async Task concurrent_first_messages_still_assemble_a_single_batch()
    {
        ExpiryItemHandler.Clear();

        using var host = await hostAsync(500.Milliseconds(), "race_items");

        var session = await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<ExpiryItem[]>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                var gate = new Barrier(2);

                await Task.WhenAll(
                    Task.Run(async () =>
                    {
                        gate.SignalAndWait();
                        await c.PublishAsync(new ExpiryItem("one"));
                    }),
                    Task.Run(async () =>
                    {
                        gate.SignalAndWait();
                        await c.PublishAsync(new ExpiryItem("two"));
                    }));
            }));

        var batches = session.Executed.MessagesOf<ExpiryItem[]>().ToArray();

        batches.Length.ShouldBe(1,
            "The two members were split across separate BatchingProcessor instances: " +
            string.Join(" | ", batches.Select(b => "[" + string.Join(",", b.Select(x => x.Name)) + "]")));
    }
}
