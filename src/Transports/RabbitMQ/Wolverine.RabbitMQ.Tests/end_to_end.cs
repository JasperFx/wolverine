using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Oakton.Resources;
using Shouldly;
using Spectre.Console;
using TestingSupport;
using Weasel.Core;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public static class RabbitTesting
{
    public static int Number;

    public static string NextQueueName()
    {
        return $"messages{++Number}";
    }

    public static string NextExchangeName()
    {
        return $"exchange{++Number}";
    }
}

public class end_to_end
{
    [Fact]
    public void rabbitmq_transport_is_exposed_as_a_resource()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var publisher = WolverineHost.For(opts =>
        {

            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName)
                .UseDurableOutbox();

            opts.Services.AddMarten(x =>
            {
                x.Connection(Servers.PostgresConnectionString);
                x.AutoCreateSchemaObjects = AutoCreate.All;
                x.DatabaseSchemaName = "sender";
            }).IntegrateWithWolverine();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        publisher.Services.GetServices<IStatefulResourceSource>().SelectMany(x => x.FindResources())
            .OfType<BrokerResource>().Any(x => x.Name == new RabbitMqTransport().Name).ShouldBeTrue();
    }

    [Fact]
    public void rabbitmq_transport_is_NOT_exposed_as_a_resource_if_external_transports_are_stubbed()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var publisher = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName)
                .UseDurableOutbox();

            opts.StubAllExternalTransports();

            opts.Services.AddMarten(x =>
            {
                x.Connection(Servers.PostgresConnectionString);
                x.AutoCreateSchemaObjects = AutoCreate.All;
                x.DatabaseSchemaName = "sender";
            }).IntegrateWithWolverine();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        publisher.Services.GetServices<IStatefulResourceSource>().SelectMany(x => x.FindResources())
            .OfType<BrokerResource>().Any(x => x.Name == new RabbitMqTransport().Name).ShouldBeFalse();
    }

    [Fact]
    public async Task send_message_to_and_receive_through_rabbitmq_with_durable_transport_option()
    {
        var queueName = "durable_test_queue_no_dlq";
        using var publisher = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName)
                .UseDurableOutbox();

            opts.Services.AddMarten(x =>
            {
                x.Connection(Servers.PostgresConnectionString);
                x.AutoCreateSchemaObjects = AutoCreate.All;
                x.DatabaseSchemaName = "sender";
            }).IntegrateWithWolverine();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });


        using var receiver = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();

            opts.ListenToRabbitQueue(queueName).PreFetchCount(10);
            opts.Services.AddSingleton<ColorHistory>();

            opts.Services.AddMarten(x =>
            {
                x.Connection(Servers.PostgresConnectionString);
                x.AutoCreateSchemaObjects = AutoCreate.All;
                x.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        await receiver.ResetResourceState();

        await publisher
            .TrackActivity()
            .AlsoTrack(receiver)
            .Timeout(30.Seconds()) // this one can be slow when it's in a group of tests
            .SendMessageAndWaitAsync(new ColorChosen { Name = "Orange" }, new DeliveryOptions
            {
                DeliverWithin = 5.Minutes()
            });


        receiver.Get<ColorHistory>().Name.ShouldBe("Orange");
    }

    [Fact]
    public async Task send_message_to_and_receive_through_rabbitmq_with_inline_receivers()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var publisher = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName)
                .SendInline();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });


        using var receiver = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision();

            opts.ListenToRabbitQueue(queueName).ProcessInline().Named(queueName);
            opts.Services.AddSingleton<ColorHistory>();


            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        await receiver.ResetResourceState();

        for (int i = 0; i < 10000; i++)
        {
            await publisher.SendAsync(new ColorChosen { Name = "blue" });
        }

        var cancellation = new CancellationTokenSource(30.Seconds());
        var queue = receiver.Get<IWolverineRuntime>().Endpoints.EndpointByName(queueName).ShouldBeOfType<RabbitMqQueue>();

        while (!cancellation.IsCancellationRequested && await queue.QueuedCountAsync() > 0)
        {
            await Task.Delay(250.Milliseconds(), cancellation.Token);
        }

        cancellation.Token.ThrowIfCancellationRequested();


    }

    [Fact]
    public async Task send_message_to_and_receive_through_rabbitmq_with_inline_receivers_and_only_listener_connection()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var publisher = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName)
                .SendInline();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);


        });


        using var receiver = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision().UseListenerConnectionOnly();

            opts.ListenToRabbitQueue(queueName).ProcessInline().Named(queueName);
            opts.Services.AddSingleton<ColorHistory>();


            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        await receiver.ResetResourceState();

        for (int i = 0; i < 10000; i++)
        {
            await publisher.SendAsync(new ColorChosen { Name = "blue" });
        }

        var cancellation = new CancellationTokenSource(30.Seconds());
        var queue = receiver.Get<IWolverineRuntime>().Endpoints.EndpointByName(queueName).ShouldBeOfType<RabbitMqQueue>();

        while (!cancellation.IsCancellationRequested && await queue.QueuedCountAsync() > 0)
        {
            await Task.Delay(250.Milliseconds(), cancellation.Token);
        }

        cancellation.Token.ThrowIfCancellationRequested();


    }

    [Fact]
    public async Task send_message_to_and_receive_through_rabbitmq_with_inline_receivers_and_only_subscriber_connection()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var publisher = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName)
                .SendInline();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);


        });


        using var receiver = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision();

            opts.ListenToRabbitQueue(queueName).ProcessInline().Named(queueName);
            opts.Services.AddSingleton<ColorHistory>();


            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        await receiver.ResetResourceState();

        for (int i = 0; i < 10000; i++)
        {
            await publisher.SendAsync(new ColorChosen { Name = "blue" });
        }

        var cancellation = new CancellationTokenSource(30.Seconds());
        var queue = receiver.Get<IWolverineRuntime>().Endpoints.EndpointByName(queueName).ShouldBeOfType<RabbitMqQueue>();

        while (!cancellation.IsCancellationRequested && await queue.QueuedCountAsync() > 0)
        {
            await Task.Delay(250.Milliseconds(), cancellation.Token);
        }

        cancellation.Token.ThrowIfCancellationRequested();


    }

    [Fact]
    public async Task reply_uri_mechanics()
    {
        var queueName1 = RabbitTesting.NextQueueName();
        var queueName2 = RabbitTesting.NextQueueName();


        using var publisher = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Publisher";

            opts.UseRabbitMq().AutoProvision();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName1)
                .UseDurableOutbox();

            opts.ListenToRabbitQueue(queueName2).UseForReplies();

            opts.Services.AddMarten(x =>
            {
                x.Connection(Servers.PostgresConnectionString);
                x.AutoCreateSchemaObjects = AutoCreate.All;
                x.DatabaseSchemaName = "sender";
            }).IntegrateWithWolverine();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        using var receiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Receiver";

            opts.UseRabbitMq().AutoProvision();

            opts.ListenToRabbitQueue(queueName1);
            opts.Services.AddSingleton<ColorHistory>();

            opts.Services.AddMarten(x =>
            {
                x.Connection(Servers.PostgresConnectionString);
                x.AutoCreateSchemaObjects = AutoCreate.All;
                x.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        var session = await publisher
            .TrackActivity()
            .AlsoTrack(receiver)
            .Timeout(2.Minutes())
            .SendMessageAndWaitAsync(new PingMessage { Number = 1 });


        // TODO -- let's make an assertion here?
        var records = session.FindEnvelopesWithMessageType<PongMessage>(MessageEventType.Received);
        records.Any(x => x.ServiceName == "Publisher").ShouldBeTrue();
    }

    [Fact]
    public async Task send_message_to_and_receive_through_rabbitmq_with_routing_key()
    {
        var queueName = RabbitTesting.NextQueueName();
        var exchangeName = RabbitTesting.NextExchangeName();

        var publisher = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq()
                .AutoProvision()
                .BindExchange(exchangeName)
                .ToQueue(queueName, "key2");

            opts.PublishAllMessages().ToRabbitExchange(exchangeName);

            opts.Services.AddResourceSetupOnStartup();
        });

        var receiver = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq()
                .AutoProvision()
                .DeclareQueue(RabbitTesting.NextQueueName())
                .BindExchange(exchangeName).ToQueue(queueName, "key2");

            opts.Services.AddSingleton<ColorHistory>();

            opts.Services.AddResourceSetupOnStartup();

            opts.ListenToRabbitQueue(queueName);
        });

        try
        {
            await publisher
                .TrackActivity()
                .AlsoTrack(receiver)
                .SendMessageAndWaitAsync(new ColorChosen { Name = "Orange" });

            receiver.Get<ColorHistory>().Name.ShouldBe("Orange");
        }
        finally
        {
            publisher.Dispose();
            receiver.Dispose();
        }
    }

    [Fact]
    public async Task schedule_send_message_to_and_receive_through_rabbitmq_with_durable_transport_option()
    {
        var queueName = RabbitTesting.NextQueueName();

        var publisher = WolverineHost.For(opts =>
        {
            opts.Durability.ScheduledJobFirstExecution = 1.Seconds();
            opts.Durability.ScheduledJobPollingTime = 1.Seconds();
            opts.ServiceName = "Publisher";

            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages().ToRabbitQueue(queueName).UseDurableOutbox();

            opts.Services.AddMarten(x =>
            {
                x.Connection(Servers.PostgresConnectionString);
                x.AutoCreateSchemaObjects = AutoCreate.All;
                x.DatabaseSchemaName = "rabbit_sender";
            }).IntegrateWithWolverine();
        });

        await publisher.ResetResourceState();

        var receiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Receiver";

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName);
            opts.Services.AddSingleton<ColorHistory>();

            opts.Services.AddMarten(x =>
            {
                x.Connection(Servers.PostgresConnectionString);
                x.AutoCreateSchemaObjects = AutoCreate.All;
                x.DatabaseSchemaName = "rabbit_receiver";
            }).IntegrateWithWolverine();
        });

        await receiver.ResetResourceState();

        try
        {
            await publisher
                .TrackActivity()
                .AlsoTrack(receiver)
                .WaitForMessageToBeReceivedAt<ColorChosen>(receiver)
                .Timeout(15.Seconds())
                .ExecuteAndWaitAsync(c => c.ScheduleAsync(new ColorChosen { Name = "Orange" }, 5.Seconds()));

            receiver.Get<ColorHistory>().Name.ShouldBe("Orange");
        }
        finally
        {
            publisher.Dispose();
            receiver.Dispose();
        }
    }

    [Fact]
    public async Task use_fan_out_exchange()
    {
        var exchangeName = "fanout1";
        var queueName1 = RabbitTesting.NextQueueName() + "e23";
        var queueName2 = RabbitTesting.NextQueueName() + "e23";
        var queueName3 = RabbitTesting.NextQueueName() + "e23";


        var publisher = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision()
                .BindExchange(exchangeName).ToQueue(queueName1)
                .BindExchange(exchangeName).ToQueue(queueName2)
                .BindExchange(exchangeName).ToQueue(queueName3);

            opts.PublishAllMessages().ToRabbitExchange(exchangeName);
        });

        var receiver1 = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName1);
            opts.Services.AddSingleton<ColorHistory>();
        });

        var receiver2 = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName2);
            opts.Services.AddSingleton<ColorHistory>();
        });

        var receiver3 = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName3);
            opts.Services.AddSingleton<ColorHistory>();
        });

        try
        {
            var session = await publisher
                .TrackActivity()
                .AlsoTrack(receiver1, receiver2, receiver3)
                .WaitForMessageToBeReceivedAt<ColorChosen>(receiver1)
                .WaitForMessageToBeReceivedAt<ColorChosen>(receiver2)
                .WaitForMessageToBeReceivedAt<ColorChosen>(receiver3)
                .SendMessageAndWaitAsync(new ColorChosen { Name = "Purple" });


            receiver1.Get<ColorHistory>().Name.ShouldBe("Purple");
            receiver2.Get<ColorHistory>().Name.ShouldBe("Purple");
            receiver3.Get<ColorHistory>().Name.ShouldBe("Purple");
        }
        finally
        {
            publisher.Dispose();
            receiver1.Dispose();
            receiver2.Dispose();
            receiver3.Dispose();
        }
    }

    [Fact]
    public async Task send_message_to_and_receive_through_rabbitmq_with_named_topic()
    {
        var queueName = RabbitTesting.NextQueueName();

        var publisher = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq().AutoProvision()
                .BindExchange("topics", ExchangeType.Topic)
                .ToQueue(queueName, "special");

            opts.PublishAllMessages().ToRabbitTopic("special", "topics");

            opts.DisableConventionalDiscovery();
        });

        var receiver = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName);

            opts.DisableConventionalDiscovery().IncludeType<SpecialTopicGuy>();
        });

        try
        {
            var message = new SpecialTopic();
            var session = await publisher
                .TrackActivity()
                .AlsoTrack(receiver)
                .SendMessageAndWaitAsync(message);


            var received = session.FindSingleTrackedMessageOfType<SpecialTopic>(MessageEventType.MessageSucceeded);
            received
                .Id.ShouldBe(message.Id);
        }
        finally
        {
            publisher.Dispose();
            receiver.Dispose();
        }
    }

    [Fact]
    public async Task use_direct_exchange_with_binding_key()
    {
        var exchangeName = "direct1";
        var queueName1 = RabbitTesting.NextQueueName() + "e23";
        var queueName2 = RabbitTesting.NextQueueName() + "e23";
        var queueName3 = RabbitTesting.NextQueueName() + "e23";
        var bindKey1 = $"{exchangeName}_{queueName1}";
        var bindKey2 = $"{exchangeName}_{queueName2}";
        var bindKey3 = $"{exchangeName}_{queueName3}";

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision()
                .BindExchange(exchangeName, ExchangeType.Direct).ToQueue(queueName1, bindKey1)
                .BindExchange(exchangeName, ExchangeType.Direct).ToQueue(queueName2, bindKey2)
                .BindExchange(exchangeName, ExchangeType.Direct).ToQueue(queueName3, bindKey3);

            opts.PublishAllMessages().ToRabbitRoutingKey(exchangeName, bindKey1);
            opts.PublishAllMessages().ToRabbitRoutingKey(exchangeName, bindKey2);
            opts.PublishAllMessages().ToRabbitRoutingKey(exchangeName, bindKey3);
        });

        using var receiver1 = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName1);
            opts.Services.AddSingleton<ColorHistory>();
        });

        using var receiver2 = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName2);
            opts.Services.AddSingleton<ColorHistory>();
        });

        using var receiver3 = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName3);
            opts.Services.AddSingleton<ColorHistory>();
        });

        var session = await publisher
            .TrackActivity()
            .AlsoTrack(receiver1, receiver2, receiver3)
            .WaitForMessageToBeReceivedAt<ColorChosen>(receiver1)
            .WaitForMessageToBeReceivedAt<ColorChosen>(receiver2)
            .WaitForMessageToBeReceivedAt<ColorChosen>(receiver3)
            .SendMessageAndWaitAsync(new ColorChosen { Name = "Purple" });


        receiver1.Get<ColorHistory>().Name.ShouldBe("Purple");
        receiver2.Get<ColorHistory>().Name.ShouldBe("Purple");
        receiver3.Get<ColorHistory>().Name.ShouldBe("Purple");
    }

    [Fact]
    public async Task use_direct_exchange()
    {
        var exchangeName = "direct2";
        var queueName = RabbitTesting.NextQueueName() + "e23";

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision()
                .BindExchange(exchangeName, ExchangeType.Direct).ToQueue(queueName);

            opts.PublishAllMessages().ToRabbitExchange(exchangeName);
        });

        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName);
            opts.Services.AddSingleton<ColorHistory>();
        });


        var session = await publisher
            .TrackActivity()
            .Timeout(30.Seconds())
            .AlsoTrack(receiver)
            .WaitForMessageToBeReceivedAt<ColorChosen>(receiver)
            .SendMessageAndWaitAsync(new ColorChosen { Name = "Purple" });


        receiver.Get<ColorHistory>().Name.ShouldBe("Purple");

    }
}

public class SpecialTopicGuy
{
    public void Handle(SpecialTopic topic)
    {
    }
}

public class ColorHandler
{
    public void Handle(ColorChosen message, ColorHistory history, Envelope envelope)
    {
        history.Name = message.Name;
        history.Envelope = envelope;
    }
}

public class ColorHistory
{
    public string Name { get; set; }
    public Envelope Envelope { get; set; }
}

public class ColorChosen
{
    public string Name { get; set; }
}

[MessageIdentity("A")]
public class TopicA
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

[MessageIdentity("B")]
public class TopicB
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

[MessageIdentity("C")]
public class TopicC
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class SpecialTopic
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

// The [MessageIdentity] attribute is only necessary
// because the projects aren't sharing types
// You would not do this if you were distributing
// message types through shared assemblies
[MessageIdentity("TryToReconnect")]
public class PingMessage
{
    public int Number { get; set; }
}

[MessageIdentity("Pong")]
public class PongMessage
{
    public int Number { get; set; }
}

public static class PongHandler
{
    // "Handle" is recognized by Wolverine as a message handling
    // method. Handler methods can be static or instance methods
    public static void Handle(PongMessage message)
    {
        AnsiConsole.MarkupLine($"[blue]Got pong #{message.Number}[/]");
    }
}

public static class PingHandler
{
    // Simple message handler for the PingMessage message type
    public static ValueTask Handle(
        // The first argument is assumed to be the message type
        PingMessage message,

        // Wolverine supports method injection similar to ASP.Net Core MVC
        // In this case though, IMessageContext is scoped to the message
        // being handled
        IMessageContext context)
    {
        AnsiConsole.MarkupLine($"[blue]Got ping #{message.Number}[/]");

        var response = new PongMessage
        {
            Number = message.Number
        };

        // This usage will send the response message
        // back to the original sender. Wolverine uses message
        // headers to embed the reply address for exactly
        // this use case
        return context.RespondToSenderAsync(response);
    }
}