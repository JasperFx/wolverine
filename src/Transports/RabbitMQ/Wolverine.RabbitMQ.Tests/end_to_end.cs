using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Spectre.Console;
using Wolverine.ComplianceTests;
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
    /// <summary>
    /// Unique per process. A bare counter is not: it restarts at zero in every process, and Bobcat
    /// partitions this project across several worker PROCESSES by class. Two tests in different
    /// processes therefore declared and bound the SAME queue and exchange names against the one
    /// shared broker and consumed each other's messages -- and because xUnit does not fix the order
    /// of tests within a class, which pair collided moved from run to run. That is why
    /// use_direct_exchange_with_binding_key and use_fan_out_exchange failed "one or the other,
    /// every run" while each passes alone.
    ///
    /// It also repeated across runs on a persistent broker, so a queue could inherit a binding from
    /// a previous run, and it could collide with the literal "exchange1"/"exchange3" used by
    /// auto_declaration_of_rabbit_resources and when_adding_bindings. GH-3763.
    /// </summary>
    private static readonly string Token = Guid.NewGuid().ToString("N")[..8];

    private static int _number;

    public static string NextQueueName()
    {
        return $"messages-{Token}-{Interlocked.Increment(ref _number)}";
    }

    public static string NextExchangeName()
    {
        return $"exchange-{Token}-{Interlocked.Increment(ref _number)}";
    }

    /// <summary>
    /// The three compliance fixtures used to build "listener{RabbitTesting.Number}" by READING the
    /// counter without advancing it, so all three asked for the same queue -- "listener0" in a
    /// fresh process -- and collided with each other and with every other worker process.
    /// </summary>
    public static string NextListenerName()
    {
        return $"listener-{Token}-{Interlocked.Increment(ref _number)}";
    }
}

// GH-3824: UNTAGGED. The last remaining failures in this class were a Wolverine bug, not flakiness.
//
// The previous triage got as far as "use_fan_out_exchange and use_direct_exchange_with_binding_key each
// pass ALONE and fail inside the class, in ~500ms on a null ColorHistory" -- all correct -- and then
// inferred the cause: that WaitForMessageToBeReceivedAt is satisfied by MessageFailed as well as
// MessageSucceeded, so a message that arrived and then failed ended the session early. That inference was
// wrong. Dumping the session instead of inferring from the assertion (which is exactly what that triage
// said to do next) showed status=Completed, ZERO exceptions, and the message marked successful -- but at
// only ONE of the three receivers.
//
// The real cause was in TrackedSession.IsCompleted(): it short-circuited on the first satisfied condition
// (Any) rather than requiring all of them, which made the All(...) check on its own last line unreachable.
// Both of these tests chain three WaitForMessageToBeReceivedAt calls for a fan-out, so the session returned
// as soon as the FIRST receiver handled the message and the assertions raced the other two handlers. Alone
// on an idle machine all three finish inside the same millisecond, which is why it only failed in-class.
//
// Two further real causes were found and fixed earlier and were prerequisites, not red herrings: the
// process-unique naming in RabbitTesting above, and the GH-3521 ApplicationAssembly pins on the receiver
// hosts.
public class end_to_end
{
    private readonly ITestOutputHelper _output;

    public end_to_end(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task rabbitmq_transport_is_exposed_as_a_resource()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var publisher = await WolverineHost.ForAsync(opts =>
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

        var sources = publisher.Services.GetServices<ISystemPart>().OfType<WolverineSystemPart>();
        foreach (var source in sources)
        {
            var resources = await source.FindResources();
            resources.OfType<BrokerResource>().Any(x => x.Name == new RabbitMqTransport().Name).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task find_endpoints_through_conventions_as_part_of_find_resources()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = GetType().Assembly;
                opts.UseRabbitMq().UseConventionalRouting();
            }).Build();
        
        var sources = host.Services.GetServices<ISystemPart>().OfType<WolverineSystemPart>();
        foreach (var source in sources)
        {
            var resources = await source.FindResources();
        }

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<RabbitMqTransport>();
        transport.Exchanges.Contains(typeof(OM1).FullNameInCode()).ShouldBeTrue();
        transport.Exchanges.Contains(typeof(OM2).FullNameInCode()).ShouldBeTrue();
        transport.Exchanges.Contains(typeof(OM3).FullNameInCode()).ShouldBeTrue();
        transport.Exchanges.Contains(typeof(OM4).FullNameInCode()).ShouldBeTrue();
    }



    [Fact]
    public async Task rabbitmq_transport_is_NOT_exposed_as_a_resource_if_external_transports_are_stubbed()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var publisher = await WolverineHost.ForAsync(opts =>
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

        var sources = publisher.Services.GetServices<ISystemPart>();
        foreach (var source in sources)
        {
            var resources = await source.FindResources();
            resources.OfType<BrokerResource>().Any(x => x.Name == new RabbitMqTransport().Name).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task send_message_to_and_receive_through_rabbitmq_with_durable_transport_option()
    {
        var queueName = "durable_test_queue_no_dlq";
        using var publisher = await WolverineHost.ForAsync(opts =>
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


        using var receiver = await WolverineHost.ForAsync(opts =>
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

        await receiver.ResetResourceState(cancellation: TestContext.Current.CancellationToken);

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
        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName)
                .SendInline();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });


        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();

            opts.ListenToRabbitQueue(queueName).ProcessInline().Named(queueName);
            opts.Services.AddSingleton<ColorHistory>();


            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        await receiver.ResetResourceState(cancellation: TestContext.Current.CancellationToken);

        for (int i = 0; i < 10000; i++)
        {
            await publisher.SendAsync(new ColorChosen { Name = "blue" });
        }

        using var cancellation = new CancellationTokenSource(30.Seconds());
        var queue = receiver.Get<IWolverineRuntime>().Endpoints.EndpointByName(queueName).ShouldBeOfType<RabbitMqQueue>();

        while (!cancellation.IsCancellationRequested && await queue.QueuedCountAsync() > 0)
        {
            await Task.Delay(250.Milliseconds(), cancellation.Token);
        }

        cancellation.Token.ThrowIfCancellationRequested();


    }
    
    [Fact]
    public async Task send_message_to_and_receive_through_rabbitmq_with_inline_receivers_and_with_CloudEvents()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName)
                .SendInline().InteropWithCloudEvents();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });


        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();

            opts.ListenToRabbitQueue(queueName).ProcessInline().Named(queueName).InteropWithCloudEvents();
            opts.Services.AddSingleton<ColorHistory>();


            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        await receiver.ResetResourceState(cancellation: TestContext.Current.CancellationToken);

        for (int i = 0; i < 10000; i++)
        {
            await publisher.SendAsync(new ColorChosen { Name = "blue" });
        }

        using var cancellation = new CancellationTokenSource(30.Seconds());
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
        using var publisher =  await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName)
                .SendInline();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            
        }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().UseListenerConnectionOnly();

                opts.ListenToRabbitQueue(queueName).ProcessInline().Named(queueName);
                opts.Services.AddSingleton<ColorHistory>();


                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await receiver.ResetResourceState(cancellation: TestContext.Current.CancellationToken);

        for (int i = 0; i < 10000; i++)
        {
            await publisher.SendAsync(new ColorChosen { Name = "blue" });
        }

        using var cancellation = new CancellationTokenSource(30.Seconds());
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
        var exchangeName = "ex_" + queueName;
        using var publisher = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().UseSenderConnectionOnly();

            opts.PublishAllMessages()
                .ToRabbitExchange(exchangeName)
                .SendInline();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        
        using var receiver = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            var rabbit = opts.UseRabbitMq().AutoProvision();

            // TODO is this a feature gap?
            rabbit.BindExchange(exchangeName).ToQueue(queueName);
            opts.ListenToRabbitQueue(queueName).ProcessInline().Named(queueName);
            opts.Services.AddSingleton<ColorHistory>();


            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        Func<IMessageContext, Task> publishing = async c =>
        {
            for (int i = 0; i < 100; i++)
            {
                await c.SendAsync(new ColorChosen { Name = "blue" });
            }
        };

        var tracked = await publisher.TrackActivity().AlsoTrack(receiver).Timeout(30.Seconds())
            .ExecuteAndWaitAsync(publishing);

        var received = tracked.Received.MessagesOf<ColorChosen>().ToList();
        received.Count.ShouldBe(100);
    }

    [Fact]
    public async Task reply_uri_mechanics()
    {
        var queueName1 = RabbitTesting.NextQueueName();
        var queueName2 = RabbitTesting.NextQueueName();


        using var publisher = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ServiceName = "Publisher";

            opts.UseRabbitMq().AutoProvision();
            
            opts.Policies.DisableConventionalLocalRouting();

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
        }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        using var receiver = await Host.CreateDefaultBuilder().UseWolverine(opts =>
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
        }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq()
                .AutoProvision()
                .BindExchange(exchangeName)
                .ToQueue(queueName, "key2");

            opts.PublishAllMessages().ToRabbitExchange(exchangeName);

            opts.Services.AddResourceSetupOnStartup();
        });

        var receiver = await WolverineHost.ForAsync(opts =>
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
                .Timeout(30.Seconds())
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

        var publisher = await WolverineHost.ForAsync(opts =>
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

        await publisher.ResetResourceState(cancellation: TestContext.Current.CancellationToken);

        var receiver = await WolverineHost.ForAsync(opts =>
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

        await receiver.ResetResourceState(cancellation: TestContext.Current.CancellationToken);

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
        var exchangeName = RabbitTesting.NextExchangeName();
        var queueName1 = RabbitTesting.NextQueueName() + "e23";
        var queueName2 = RabbitTesting.NextQueueName() + "e23";
        var queueName3 = RabbitTesting.NextQueueName() + "e23";


        var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision()
                .BindExchange(exchangeName).ToQueue(queueName1)
                .BindExchange(exchangeName).ToQueue(queueName2)
                .BindExchange(exchangeName).ToQueue(queueName3);

            opts.PublishAllMessages().ToRabbitExchange(exchangeName);
        });

        var receiver1 = await WolverineHost.ForAsync(opts =>
        {
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName1);
            opts.Services.AddSingleton<ColorHistory>();
        });

        var receiver2 = await WolverineHost.ForAsync(opts =>
        {
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName2);
            opts.Services.AddSingleton<ColorHistory>();
        });

        var receiver3 = await WolverineHost.ForAsync(opts =>
        {
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName3);
            opts.Services.AddSingleton<ColorHistory>();
        });

        try
        {
            var session = await publisher
                .TrackActivity()
                .Timeout(30.Seconds())
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

        var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision()
                .BindExchange("topics", ExchangeType.Topic)
                .ToQueue(queueName, "special");

            opts.PublishAllMessages().ToRabbitTopic("special", "topics");

            opts.DisableConventionalDiscovery();
        });

        var receiver = await WolverineHost.ForAsync(opts =>
        {
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName);

            opts.DisableConventionalDiscovery().IncludeType<SpecialTopicGuy>();
        });

        try
        {
            var message = new SpecialTopic();
            var session = await publisher
                .TrackActivity()
                .Timeout(30.Seconds())
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
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName1);
            opts.Services.AddSingleton<ColorHistory>();
        });

        using var receiver2 = await WolverineHost.ForAsync(opts =>
        {
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName2);
            opts.Services.AddSingleton<ColorHistory>();
        });

        using var receiver3 = await WolverineHost.ForAsync(opts =>
        {
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName3);
            opts.Services.AddSingleton<ColorHistory>();
        });

        var session = await publisher
            .TrackActivity()
            .Timeout(30.Seconds())
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
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

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
    
    
    [Fact]
    public async Task use_exchange_to_exchange_binding()
    {
        var sourceExchange = RabbitTesting.NextExchangeName();
        var destinationExchange = RabbitTesting.NextExchangeName();
        var queueName = RabbitTesting.NextQueueName();

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision()
                // Bind source exchange to destination exchange
                .BindExchange(sourceExchange).ToExchange(destinationExchange, "e2e.key")
                // Bind destination exchange to a queue so we can consume
                .BindExchange(destinationExchange).ToQueue(queueName, "e2e.key");

            opts.PublishAllMessages().ToRabbitExchange(sourceExchange);
        });

        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName);
            opts.Services.AddSingleton<ColorHistory>();
        });

        var session = await publisher
            .TrackActivity()
            .Timeout(30.Seconds())
            .AlsoTrack(receiver)
            .WaitForMessageToBeReceivedAt<ColorChosen>(receiver)
            .SendMessageAndWaitAsync(new ColorChosen { Name = "Blue" });

        receiver.Get<ColorHistory>().Name.ShouldBe("Blue");
    }

    [Fact]
    public async Task use_exchange_to_exchange_binding_via_declare_exchange()
    {
        var sourceExchange = RabbitTesting.NextExchangeName();
        var destinationExchange = RabbitTesting.NextExchangeName();
        var queueName = RabbitTesting.NextQueueName();

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision()
                .DeclareExchange(destinationExchange, exchange =>
                {
                    exchange.ExchangeType = ExchangeType.Fanout;
                    exchange.BindExchange(sourceExchange);
                    exchange.BindQueue(queueName);
                });

            opts.PublishAllMessages().ToRabbitExchange(sourceExchange);
        });

        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            // GH-3521: the application assembly is a process-wide value pinned by whichever host
            // started FIRST in the process. Without this, these receivers discovered NO handlers
            // ("Wolverine found no handlers" in the log), ColorChosen failed for want of a handler,
            // and WaitForMessageToBeReceivedAt completed anyway -- it is satisfied by MessageFailed
            // as well as MessageSucceeded -- so the test fell through to a null ColorHistory in
            // ~500ms rather than timing out. GH-3763.
            opts.ApplicationAssembly = GetType().Assembly;

            opts.UseRabbitMq();

            opts.ListenToRabbitQueue(queueName);
            opts.Services.AddSingleton<ColorHistory>();
        });

        var session = await publisher
            .TrackActivity()
            .Timeout(30.Seconds())
            .AlsoTrack(receiver)
            .WaitForMessageToBeReceivedAt<ColorChosen>(receiver)
            .SendMessageAndWaitAsync(new ColorChosen { Name = "Green" });

        receiver.Get<ColorHistory>().Name.ShouldBe("Green");
    }

    [Fact]
    public async Task request_reply_from_within_handler()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToRabbitQueue(queueName);

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

            opts.DisableConventionalDiscovery()
                .IncludeType(typeof(RequestColorsHandler))
                .IncludeType(typeof(ColorResponseHandler));
        });


        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.DisableConventionalDiscovery()
                .IncludeType(typeof(ColorRequestHandler));
            
            opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();

            opts.ListenToRabbitQueue(queueName);
        });

        await receiver.ResetResourceState(cancellation: TestContext.Current.CancellationToken);

        await publisher
            .TrackActivity()
            .AlsoTrack(receiver)
            .Timeout(30.Seconds()) // this one can be slow when it's in a group of tests
            .InvokeMessageAndWaitAsync(new RequestColors(["red", "green", "blue", "orange"]));
            //.InvokeMessageAndWaitAsync(new RequestColors(["red"]));
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
    public string Name { get; set; } = null!;
    public Envelope Envelope { get; set; } = null!;
}

public class ColorChosen
{
    public string Name { get; set; } = null!;
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

public record ColorRequest(string Color);
public record ColorResponse(string Color);

public static class ColorRequestHandler
{
    public static async Task<ColorResponse> Handle(ColorRequest request)
    {
        await Task.Delay(Random.Shared.Next(0, 500).Milliseconds());
        return new ColorResponse(request.Color);
    }
}

public static class ColorResponseHandler
{
    public static void Handle(ColorResponse response) => Debug.WriteLine("Got color response for " + response.Color);
}

public record RequestColors(string[] Colors);

public static class RequestColorsHandler
{
    public static async Task HandleAsync(RequestColors message, IMessageBus bus)
    {
        for (int i = 0; i < message.Colors.Length; i++)
        {
            var response = await bus.InvokeAsync<ColorResponse>(new ColorRequest(message.Colors[i]), timeout:30.Seconds());
            response.Color.ShouldBe(message.Colors[i]);
        }
    }
}

public record OM1 : IMessage;
public record OM2 : IMessage;
public record OM3 : IMessage;
public record OM4 : IMessage;