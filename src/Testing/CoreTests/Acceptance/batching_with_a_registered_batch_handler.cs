using System.Collections.Concurrent;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// Batching used to demand a DISCOVERED chain for the batch type (<c>HandlerGraph.ChainFor</c>), so a batch
/// handler registered with <see cref="WolverineOptions.AddMessageHandler(Type, IMessageHandler)" /> — a
/// pre-generated or hand-written <see cref="MessageHandler" /> — was refused on the first message with
/// "there is no known handler for T[]", and every element went to the error queue. The batch probe policy
/// had the same lookup and skipped such a handler silently. Found by CritterWatch, whose embedded console
/// registers pre-generated handlers so its host needs no runtime compilation.
/// </summary>
public class batching_with_a_registered_batch_handler : IAsyncLifetime
{
    private IHost theHost = null!;
    private readonly RegisteredItemBatchHandler theHandler = new();

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();

                opts.AddMessageHandler(typeof(RegisteredItem[]), theHandler);

                opts.BatchMessagesOf<RegisteredItem>(batching =>
                {
                    batching.TriggerTime = 100.Milliseconds();
                    batching.ProbeIndividuallyAfter(2);
                });
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void the_element_type_is_batched_onto_the_registered_handlers_chain()
    {
        var processor = theHost.GetRuntime()
            .As<IExecutorFactory>()
            .BuildFor(typeof(RegisteredItem))
            .ShouldBeOfType<Executor>()
            .Handler
            .ShouldBeOfType<BatchingProcessor<RegisteredItem>>();

        processor.Chain.ShouldBeSameAs(theHandler.Chain);
    }

    [Fact]
    public void the_probe_policy_reaches_the_registered_handlers_chain()
    {
        // Before the fix this rule was skipped without a word, because the lookup found no discovered chain.
        theHandler.Chain!.Failures.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task batches_are_delivered_to_the_registered_handler()
    {
        var items = new[] { new RegisteredItem("one"), new RegisteredItem("two"), new RegisteredItem("three") };

        await theHost.TrackActivity()
            .WaitForMessageToBeReceivedAt<RegisteredItem[]>(theHost)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async context =>
            {
                foreach (var item in items) await context.PublishAsync(item);
            }));

        theHandler.Received.SelectMany(x => x).OrderBy(x => x.Name).ShouldBe(items.OrderBy(x => x.Name));
    }
}

public record RegisteredItem(string Name);

public class RegisteredItemBatchHandler : MessageHandler<RegisteredItem[]>
{
    public ConcurrentQueue<RegisteredItem[]> Received { get; } = new();

    protected override Task HandleAsync(RegisteredItem[] message, MessageContext context, CancellationToken cancellation)
    {
        Received.Enqueue(message);
        return Task.CompletedTask;
    }
}
