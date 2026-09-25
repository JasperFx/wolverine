using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests.Bugs;

// GH-4615: with the default conventional routing, every node in a cluster used to provision its own
// "{topic}.{AssignedNodeNumber}" subscription for a competing-consumer listener. Pub/Sub delivers a
// copy of every message to every subscription, so a "competing consumers" listener actually fanned
// each message out to every node. A subscription per node is now strictly opt in.
public class Bug_4615_subscription_per_node
{
    private static PubsubTransport createTransport(int assignedNodeNumber)
    {
        return new PubsubTransport
        {
            ProjectId = "wolverine",
            PublisherApiClient = Substitute.For<PublisherServiceApiClient>(),
            SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>(),
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
            AssignedNodeNumber = assignedNodeNumber
        };
    }

    private static async Task<string> subscriptionIdOnNode(int assignedNodeNumber)
    {
        var endpoint = new PubsubEndpoint("foo", createTransport(assignedNodeNumber))
        {
            IsListener = true,
            ListenerScope = ListenerScope.CompetingConsumers
        };

        await endpoint.SetupAsync(NullLogger.Instance);

        return endpoint.Server.Subscription.Name.SubscriptionId;
    }

    [Fact]
    public async Task competing_consumer_listeners_on_different_nodes_share_one_subscription()
    {
        var onNode1 = await subscriptionIdOnNode(1);
        var onNode2 = await subscriptionIdOnNode(2);

        // Was "foo.1" and "foo.2" -- two subscriptions, so every message was delivered twice
        onNode2.ShouldBe(onNode1);
    }

    [Fact]
    public async Task a_message_published_to_a_conventionally_routed_topic_is_handled_once_across_the_cluster()
    {
        Assert.SkipUnless(await TestingExtensions.IsEmulatorAvailable(), "Pub/Sub emulator is not available");

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting().AutoProvision().UseConventionalRouting();
                opts.Discovery.DisableConventionalDiscovery();
                opts.ServiceName = "Sender";
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        // Two nodes of the SAME service, all defaults: conventional routing, no customization
        using var node1 = await startReceiverNode();
        using var node2 = await startReceiverNode();

        var session = await sender
            .TrackActivity()
            .AlsoTrack(node1, node2)
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new Bug4615Message(Guid.NewGuid()));

        var handled = session.AllRecordsInOrder()
            .Where(x => x.Envelope!.Message is Bug4615Message)
            .Count(x => x.MessageEventType == MessageEventType.MessageSucceeded);

        // Was handled twice, once by each node
        handled.ShouldBe(1);

        await sender.StopAsync(TestContext.Current.CancellationToken);
        await node1.StopAsync(TestContext.Current.CancellationToken);
        await node2.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task every_node_handles_the_message_when_opted_into_a_subscription_per_node()
    {
        Assert.SkipUnless(await TestingExtensions.IsEmulatorAvailable(), "Pub/Sub emulator is not available");

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting().AutoProvision().UseConventionalRouting();
                opts.Discovery.DisableConventionalDiscovery();
                opts.ServiceName = "Sender";
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        using var node1 = await startReceiverNode<Bug4615BroadcastMessageHandler>(subscriptionPerNode: true);
        using var node2 = await startReceiverNode<Bug4615BroadcastMessageHandler>(subscriptionPerNode: true);

        var session = await sender
            .TrackActivity()
            .AlsoTrack(node1, node2)
            .IncludeExternalTransports()
            .Timeout(30.Seconds())

            // One message handled twice. WaitForExecutionOf<T>() counts distinct messages, so it cannot express this,
            // and without a condition the session can finish before the second node has its copy
            .WaitForCondition(new HandledAtLeast<Bug4615BroadcastMessage>(2))
            .PublishMessageAndWaitAsync(new Bug4615BroadcastMessage(Guid.NewGuid()));

        var handled = session.AllRecordsInOrder()
            .Where(x => x.Envelope!.Message is Bug4615BroadcastMessage)
            .Count(x => x.MessageEventType == MessageEventType.MessageSucceeded);

        handled.ShouldBe(2);

        await sender.StopAsync(TestContext.Current.CancellationToken);
        await node1.StopAsync(TestContext.Current.CancellationToken);
        await node2.StopAsync(TestContext.Current.CancellationToken);
    }

    private static Task<IHost> startReceiverNode()
    {
        return startReceiverNode<Bug4615MessageHandler>(subscriptionPerNode: false);
    }

    private static Task<IHost> startReceiverNode<THandler>(bool subscriptionPerNode)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting().AutoProvision().AutoPurgeOnStartup().UseConventionalRouting(x =>
                {
                    if (subscriptionPerNode)
                    {
                        x.ConfigureListeners((listener, _) => listener.SubscriptionPerNode());
                    }
                });
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<THandler>();
                opts.ServiceName = "Receiver";
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }
}

public record Bug4615Message(Guid Id);

public class Bug4615MessageHandler
{
    public void Handle(Bug4615Message message)
    {
    }
}

public record Bug4615BroadcastMessage(Guid Id);

public class HandledAtLeast<T>(int count) : ITrackedCondition
{
    private int _handled;

    public void Record(EnvelopeRecord record)
    {
        if (record.Message is T && record.MessageEventType == MessageEventType.MessageSucceeded)
        {
            Interlocked.Increment(ref _handled);
        }
    }

    public bool IsCompleted() => Volatile.Read(ref _handled) >= count;

    public override string ToString() => $"{typeof(T).Name} handled at least {count} time(s), saw {_handled}";
}

public class Bug4615BroadcastMessageHandler
{
    public void Handle(Bug4615BroadcastMessage message)
    {
    }
}
