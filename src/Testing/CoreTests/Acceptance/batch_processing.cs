using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class batch_processing : IAsyncLifetime
{
    private IHost theHost;

    public async Task InitializeAsync()
    {
        #region sample_configuring_batch_processing

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.BatchMessagesOf<Item>(batching =>
                {
                    // Really the maximum batch size
                    batching.BatchSize = 500;
                    
                    // You can alternatively override the local queue
                    // for the batch publishing. 
                    batching.LocalExecutionQueueName = "items";

                    // We can tell Wolverine to wait longer for incoming
                    // messages before kicking out a batch if there
                    // are fewer waiting messages than the maximum
                    // batch size
                    batching.TriggerTime = 1.Seconds();
                    
                })
                    
                    // The object returned here is the local queue configuration that
                    // will handle the batched messages. This may be useful for fine
                    // tuning the behavior of the batch processing
                    .Sequential();
            }).StartAsync();

        #endregion
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
    }

    [Fact]
    public void add_the_batch_configuration_to_the_options()
    {
        var options = theHost.GetRuntime().Options;
        var batching = options.BatchDefinitions.Single();
        batching.ElementType.ShouldBe(typeof(Item));
        batching.BatchSize.ShouldBe(500);
    }

    [Fact]
    public void connects_to_the_local_queue()
    {
        var runtime = theHost.GetRuntime();
        var localQueue = runtime.Endpoints.EndpointFor(new Uri("local://items"));
        localQueue.ExecutionOptions.MaxDegreeOfParallelism.ShouldBe(1);
    }

    [Fact]
    public void create_batched_message_handler_for_the_element_type()
    {
        var runtime = theHost.GetRuntime();
        var handler = runtime
            .As<IExecutorFactory>()
            .BuildFor(typeof(Item))
            .ShouldBeOfType<Executor>()
            .Handler
            .ShouldBeOfType<BatchingProcessor<Item>>();

        handler.Chain.MessageType.ShouldBe(typeof(Item[]));
        handler.Queue.Uri.ShouldBe(new Uri("local://items"));
    }

    [Fact]
    public void can_determine_a_local_route_for_the_element_type()
    {
        var runtime = theHost.GetRuntime();
        var messageRouter = runtime.RoutingFor(typeof(Item));
        messageRouter.ShouldBeOfType<MessageRouter<Item>>()
            .Routes.Single().ShouldBeOfType<MessageRoute>()
            .Sender.Destination.ShouldBe(new Uri("local://items"));
    }

    #region sample_send_end_to_end_with_batch

    [Fact]
    public async Task send_end_to_end_with_batch()
    {
        // Items to publish
        var item1 = new Item("one");
        var item2 = new Item("two");
        var item3 = new Item("three");
        var item4 = new Item("four");

        Func<IMessageContext, Task> publish = async c =>
        {
            // I'm publishing the 4 items in sequence
            await c.PublishAsync(item1);
            await c.PublishAsync(item2);
            await c.PublishAsync(item3);
            await c.PublishAsync(item4);
        };

        // This is the "act" part of the test
        var session = await theHost.TrackActivity()
            
            // Wolverine testing helper to "wait" until
            // the tracking receives a message of Item[]
            .WaitForMessageToBeReceivedAt<Item[]>(theHost)
            .ExecuteAndWaitAsync(publish);

        // The four Item messages should be processed as a single 
        // batch message
        var items = session.Executed.SingleMessage<Item[]>();

        items.Length.ShouldBe(4);
        items.ShouldContain(item1);
        items.ShouldContain(item2);
        items.ShouldContain(item3);
        items.ShouldContain(item4);
    }

    #endregion
}

public record NoItem(string Name);

#region sample_batch_processing_item

public record Item(string Name);

#endregion

#region sample_batch_processing_handler

public static class ItemHandler
{
    public static void Handle(Item[] items)
    {
        // Handle this just like a normal message handler,
        // just that the message type is Item[]
    }
}

#endregion