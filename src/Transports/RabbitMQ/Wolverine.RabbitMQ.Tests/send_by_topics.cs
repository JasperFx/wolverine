using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class send_by_topics : IDisposable
{
    private readonly IHost theGreenReceiver;
    private readonly IHost theBlueReceiver;
    private readonly IHost theSender;
    private readonly IHost theThirdReceiver;

    public send_by_topics()
    {
        #region sample_binding_topics_and_topic_patterns_to_queues

        theSender = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq("host=localhost;port=5672").AutoProvision();
                opts.PublishAllMessages().ToRabbitTopics("wolverine.topics", exchange =>
                {
                    exchange.BindTopic("color.green").ToQueue("green");
                    exchange.BindTopic("color.blue").ToQueue("blue");
                    exchange.BindTopic("color.*").ToQueue("all");
                });

                opts.PublishMessagesToRabbitMqExchange<RoutedMessage>("wolverine.topics", m => m.TopicName);
            }).Start();

        #endregion

        theGreenReceiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Green";
            opts.ListenToRabbitQueue("green");
            opts.UseRabbitMq();
        });

        theBlueReceiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Blue";
            opts.ListenToRabbitQueue("blue");
            opts.UseRabbitMq();
        });

        theThirdReceiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Third";
            opts.ListenToRabbitQueue("all");
            opts.UseRabbitMq();
        });
    }

    public void Dispose()
    {
        theSender?.Dispose();
        theGreenReceiver?.Dispose();
        theBlueReceiver?.Dispose();
        theThirdReceiver?.Dispose();
    }

    [Fact]
    public void topic_route_creates_descriptor()
    {
        var route = theSender.GetRuntime().RoutingFor(typeof(PurpleMessage)).Routes.Single();

        var descriptor = route.Describe();
        descriptor.Endpoint.ShouldBe(new Uri("rabbitmq://exchange/wolverine.topics"));
        descriptor.ContentType.ShouldBe("application/json");
    }
    
    [Fact]
    public void topic_name_needs_to_be_set_on_envelope_as_part_of_routing()
    {
        // Really a global Wolverine behavior test, but using Rabbit MQ as the subject
        // Caused by https://github.com/JasperFx/wolverine/issues/1100
        var runtime = theSender.GetRuntime();

        var router = runtime.RoutingFor(typeof(PurpleMessage));
        var envelopes = router.RouteForPublish(new PurpleMessage(), null);

        var envelope = envelopes.Single();
        envelope.TopicName.ShouldBe(TopicRouting.DetermineTopicName(typeof(PurpleMessage)));
    }

    internal async Task send_by_topic_sample()
    {
        #region sample_send_to_topic

        var publisher = theSender.Services
            .GetRequiredService<IMessageBus>();

        await publisher.BroadcastToTopicAsync("color.purple", new Message1());

        #endregion
    }

    [Fact]
    public async Task send_by_message_topic()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .SendMessageAndWaitAsync(new PurpleMessage());

        session.FindEnvelopesWithMessageType<PurpleMessage>()
            .Where(x => x.MessageEventType == MessageEventType.Received)
            .Select(x => x.ServiceName)
            .Single().ShouldBe("Third");
    }

    [Fact]
    public async Task send_by_message_topic_to_multiple_listeners()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .SendMessageAndWaitAsync(new FirstMessage());

        session.FindEnvelopesWithMessageType<FirstMessage>()
            .Where(x => x.MessageEventType == MessageEventType.Received)
            .Select(x => x.ServiceName)
            .OrderBy(x => x).ShouldHaveTheSameElementsAs("Blue", "Third");
    }

    [Fact]
    public async Task send_by_explicit_topic()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .BroadcastMessageToTopicAndWaitAsync("color.green", new PurpleMessage());

        session.FindEnvelopesWithMessageType<PurpleMessage>()
            .Where(x => x.MessageEventType == MessageEventType.Received)
            .Select(x => x.ServiceName)
            .OrderBy(x => x)
            .ShouldHaveTheSameElementsAs("Green", "Third");
    }

    [Fact] // this is occasionally failing with timeouts when running in combination with the entire suite
    public async Task send_by_explicit_topic_2()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .BroadcastMessageToTopicAndWaitAsync("color.blue", new PurpleMessage());

        session.FindEnvelopesWithMessageType<PurpleMessage>()
            .Where(x => x.MessageEventType == MessageEventType.Received)
            .Select(x => x.ServiceName)
            .OrderBy(x => x)
            .ShouldHaveTheSameElementsAs("Blue", "Third");
    }

    [Fact]
    public async Task send_to_topic_with_delay()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<FirstMessage>(theBlueReceiver)
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .InvokeMessageAndWaitAsync(new TriggerTopicMessage());
    }

    [Fact]
    public async Task publish_by_user_message_topic_logic()
    {
        var routed = new RoutedMessage { TopicName = "color.blue" };

        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<RoutedMessage>(theBlueReceiver)
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .SendMessageAndWaitAsync(routed);

        var record = session.Received.RecordsInOrder().Single(x => x.ServiceName == "Blue");

        record.Envelope.Message.ShouldBeOfType<RoutedMessage>()
            .Id.ShouldBe(routed.Id);
    }

    [Fact]
    public async Task publish_by_user_message_topic_logic_and_delay()
    {
        var routed = new RoutedMessage { TopicName = "color.blue" };

        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(15.Seconds())
            .WaitForMessageToBeReceivedAt<RoutedMessage>(theBlueReceiver)
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .SendMessageAndWaitAsync(routed, new DeliveryOptions{ScheduleDelay = 3.Seconds()});

        var record = session.Received.RecordsInOrder().Single(x => x.ServiceName == "Blue");
         record.Envelope.Message.ShouldBeOfType<RoutedMessage>()
            .Id.ShouldBe(routed.Id);
    }
}

public class send_by_topics_durable : IDisposable
{
    private readonly IHost theGreenReceiver;
    private readonly IHost theBlueReceiver;
    private readonly IHost theSender;
    private readonly IHost theThirdReceiver;

    public send_by_topics_durable()
    {

        theSender = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "sender");

                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.UseRabbitMq("host=localhost;port=5672").AutoProvision();
                opts.PublishAllMessages().ToRabbitTopics("wolverine.topics", exchange =>
                {
                    exchange.BindTopic("color.green").ToQueue("green");
                    exchange.BindTopic("color.blue").ToQueue("blue");
                    exchange.BindTopic("color.*").ToQueue("all");
                });

                opts.PublishMessagesToRabbitMqExchange<RoutedMessage>("wolverine.topics", m => m.TopicName);
            }).Start();

        theGreenReceiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Green";
            opts.ListenToRabbitQueue("green");
            opts.UseRabbitMq();
        });

        theBlueReceiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Blue";
            opts.ListenToRabbitQueue("blue");
            opts.UseRabbitMq();
        });

        theThirdReceiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Third";
            opts.ListenToRabbitQueue("all");
            opts.UseRabbitMq();
        });
    }

    public void Dispose()
    {
        theSender?.Dispose();
        theGreenReceiver?.Dispose();
        theBlueReceiver?.Dispose();
        theThirdReceiver?.Dispose();
    }

    internal async Task send_by_topic_sample()
    {
        #region sample_send_to_topic

        var publisher = theSender.Services
            .GetRequiredService<IMessageBus>();

        await publisher.BroadcastToTopicAsync("color.purple", new Message1());

        #endregion
    }

    [Fact]
    public async Task send_by_message_topic()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .SendMessageAndWaitAsync(new PurpleMessage());

        session.FindEnvelopesWithMessageType<PurpleMessage>()
            .Where(x => x.MessageEventType == MessageEventType.Received)
            .Select(x => x.ServiceName)
            .Single().ShouldBe("Third");
    }

    [Fact]
    public async Task send_by_message_topic_to_multiple_listeners()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .SendMessageAndWaitAsync(new FirstMessage());

        session.FindEnvelopesWithMessageType<FirstMessage>()
            .Where(x => x.MessageEventType == MessageEventType.Received)
            .Select(x => x.ServiceName)
            .OrderBy(x => x).ShouldHaveTheSameElementsAs("Blue", "Third");
    }

    [Fact]
    public async Task send_by_explicit_topic()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .BroadcastMessageToTopicAndWaitAsync("color.green", new PurpleMessage());

        session.FindEnvelopesWithMessageType<PurpleMessage>()
            .Where(x => x.MessageEventType == MessageEventType.Received)
            .Select(x => x.ServiceName)
            .OrderBy(x => x)
            .ShouldHaveTheSameElementsAs("Green", "Third");
    }

    [Fact] // this is occasionally failing with timeouts when running in combination with the entire suite
    public async Task send_by_explicit_topic_2()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .BroadcastMessageToTopicAndWaitAsync("color.blue", new PurpleMessage());

        session.FindEnvelopesWithMessageType<PurpleMessage>()
            .Where(x => x.MessageEventType == MessageEventType.Received)
            .Select(x => x.ServiceName)
            .OrderBy(x => x)
            .ShouldHaveTheSameElementsAs("Blue", "Third");
    }

    [Fact]
    public async Task send_to_topic_with_delay()
    {
        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<FirstMessage>(theBlueReceiver)
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .InvokeMessageAndWaitAsync(new TriggerTopicMessage());
    }

    [Fact]
    public async Task publish_by_user_message_topic_logic()
    {
        var routed = new RoutedMessage { TopicName = "color.blue" };

        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<RoutedMessage>(theBlueReceiver)
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .SendMessageAndWaitAsync(routed);

        var record = session.Received.RecordsInOrder().Single(x => x.ServiceName == "Blue");

        record.Envelope.Message.ShouldBeOfType<RoutedMessage>()
            .Id.ShouldBe(routed.Id);
    }

    [Fact]
    public async Task publish_by_user_message_topic_logic_and_delay()
    {
        var routed = new RoutedMessage { TopicName = "color.blue" };

        var session = await theSender
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(15.Seconds())
            .WaitForMessageToBeReceivedAt<RoutedMessage>(theBlueReceiver)
            .AlsoTrack(theGreenReceiver, theBlueReceiver, theThirdReceiver)
            .SendMessageAndWaitAsync(routed, new DeliveryOptions{ScheduleDelay = 3.Seconds()});

        var record = session.Received.RecordsInOrder().Single(x => x.ServiceName == "Blue");

        record.Envelope.Message.ShouldBeOfType<RoutedMessage>()
            .Id.ShouldBe(routed.Id);
    }
}

[Topic("color.purple")]
public class PurpleMessage;

#region sample_using_topic_attribute

[Topic("color.blue")]
public class FirstMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

#endregion

public class SecondMessage : FirstMessage;

public class ThirdMessage : FirstMessage;

public class RoutedMessage
{
    public string TopicName { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class TriggerTopicMessage;

public class MessagesHandler
{
    public static void Handle(RoutedMessage message)
    {
    }

    public object Handle(TriggerTopicMessage message)
    {
        return new FirstMessage().ToTopic("color.blue", new DeliveryOptions { ScheduleDelay = 3.Seconds() });
    }

    public void Handle(FirstMessage message)
    {
    }

    public void Handle(SecondMessage message)
    {
    }

    public void Handle(ThirdMessage message)
    {
    }

    public void Handle(PurpleMessage message)
    {
    }
}