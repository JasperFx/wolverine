using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Tests;

public class DocumentationSamples
{
    public async Task bootstrapping()
    {
        #region sample_basic_connection_to_azure_service_bus
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString)

                // Let Wolverine try to initialize any missing queues
                // on the first usage at runtime
                .AutoProvision()

                // Direct Wolverine to purge all queues on application startup.
                // This is probably only helpful for testing
                .AutoPurgeOnStartup();

            // Or if you need some further specification...
            opts.UseAzureServiceBus(azureServiceBusConnectionString,
                azure => { azure.RetryOptions.Mode = ServiceBusRetryMode.Exponential; });
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task bootstrapping_with_the_emulator()
    {
        #region sample_using_azure_service_bus_emulator

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // Connect to a locally running Azure Service Bus emulator using the
            // standard emulator ports (AMQP on 5672, management on 5300)
            opts.UseAzureServiceBusEmulator()

                // The emulator starts out empty, so let Wolverine build
                // any queues, topics, or subscriptions it needs
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToAzureServiceBusQueue("my-queue");
            opts.PublishAllMessages().ToAzureServiceBusQueue("my-queue");
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task bootstrapping_with_the_emulator_and_explicit_connection_strings()
    {
        #region sample_using_azure_service_bus_emulator_with_connection_strings

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // If you've mapped the emulator to non-standard ports, pass both the
            // messaging (AMQP) and management (HTTP) connection strings explicitly
            opts.UseAzureServiceBusEmulator(
                    "Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
                    "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;")
                .AutoProvision()
                .AutoPurgeOnStartup();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task bootstrapping_with_the_emulator_and_cleanup()
    {
        #region sample_using_azure_service_bus_emulator_with_cleanup

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.UseAzureServiceBusEmulator()

                // CAUTION! This deletes *every* queue and topic in the connected
                // namespace at startup. It is opt in, and is only meant for the
                // emulator or a throwaway namespace. Never turn this on against
                // a real Azure Service Bus namespace you care about
                .DeleteAllExistingObjectsOnStartup()

                .AutoProvision();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task azure_service_bus_session_identifiers()
    {
        #region sample_using_azure_service_bus_session_identifiers

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusEmulator()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("send_and_receive");
                opts.PublishMessage<AsbMessage1>().ToAzureServiceBusQueue("send_and_receive");

                opts.ListenToAzureServiceBusQueue("fifo1")

                    // Require session identifiers with this queue
                    .RequireSessions()

                    // This controls the Wolverine handling to force it to process
                    // messages sequentially
                    .Sequential();

                opts.PublishMessage<AsbMessage2>()
                    .ToAzureServiceBusQueue("fifo1");

                opts.PublishMessage<AsbMessage3>().ToAzureServiceBusTopic("asb3").SendInline();
                opts.ListenToAzureServiceBusSubscription("asb3")
                    .FromTopic("asb3")

                    // Require sessions on this subscription
                    .RequireSessions(1)

                    .ProcessInline();
            }).StartAsync();

        #endregion
    }

    public async Task disable_system_queues()
    {
        #region sample_disable_system_queues_in_azure_service_bus

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string")
                    .AutoProvision().AutoPurgeOnStartup()
                    .SystemQueuesAreEnabled(false);

                opts.ListenToAzureServiceBusQueue("send_and_receive");

                opts.PublishAllMessages().ToAzureServiceBusQueue("send_and_receive");
            }).StartAsync();

        #endregion
    }

    public async Task topic_and_subscription_conventional_routing()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_using_topic_and_subscription_conventional_routing_with_azure_service_bus

                opts.UseAzureServiceBusEmulator()
                    .UseTopicAndSubscriptionConventionalRouting(convention =>
                    {
                        // Optionally control every aspect of the convention and
                        // its applicability to types
                        // as well as overriding any listener, sender, topic, or subscription
                        // options

                        // Can't use the full name because of limitations on name length
                        convention.SubscriptionNameForListener(t => t.Name.ToLowerInvariant());
                        convention.TopicNameForListener(t => t.Name.ToLowerInvariant());
                        convention.TopicNameForSender(t => t.Name.ToLowerInvariant());
                    })

                    .AutoProvision()
                    .AutoPurgeOnStartup();

                #endregion
            }).StartAsync();
    }

    public async Task configuring_queues()
    {
        #region sample_configuring_azure_service_bus_queues
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()

                // Alter how a queue should be provisioned by Wolverine
                .ConfigureQueue("outgoing", options => { options.AutoDeleteOnIdle = 5.Minutes(); });

            // Or do the same thing when creating a listener
            opts.ListenToAzureServiceBusQueue("incoming")
                .ConfigureQueue(options => { options.MaxDeliveryCount = 5; });

            // Or as part of a subscription
            opts.PublishAllMessages()
                .ToAzureServiceBusQueue("outgoing")
                .ConfigureQueue(options => { options.LockDuration = 3.Seconds(); })

                // You may need to change the maximum number of messages
                // in message batches depending on the size of your messages
                // if you hit maximum data constraints
                .MessageBatchSize(50);
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task configuring_a_listener()
    {
        #region sample_configuring_an_azure_service_bus_listener
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            opts.ListenToAzureServiceBusQueue("incoming")

                // Customize how many messages to retrieve at one time
                .MaximumMessagesToReceive(100)

                // Customize how long the listener will wait for more messages before
                // processing a batch
                .MaximumWaitTime(3.Seconds())

                // Add a circuit breaker for systematic failures
                .CircuitBreaker()

                // And all the normal Wolverine options you'd expect
                .BufferedInMemory();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task configuring_processor_options()
    {
        #region sample_configuring_azure_service_bus_processor_options
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            opts.ListenToAzureServiceBusQueue("incoming")

                // Inline listeners create an Azure Service Bus ServiceBusProcessor. By default the
                // Azure SDK only renews the message lock for five minutes, so an inline handler that
                // runs longer than that loses its lock and the message is redelivered. Raise the
                // renewal window here so long-running inline handlers keep their lock.
                .ConfigureProcessor(processorOptions =>
                {
                    processorOptions.MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(30);
                })

                // Run the handler inline against the ServiceBusProcessor
                .ProcessInline();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task configuring_prefetch_count()
    {
        #region sample_configuring_azure_service_bus_prefetch_count
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()

                // Optionally set a transport-wide default prefetch count that every
                // Azure Service Bus listener will inherit unless overridden
                .PrefetchCount(50);

            opts.ListenToAzureServiceBusQueue("incoming")

                // Have the Azure Service Bus client eagerly buffer up to 100 messages
                // on the client for just this queue, overriding the transport default.
                // Size this relative to MaximumMessagesToReceive and how fast your
                // handlers actually are -- prefetched messages age against the message
                // lock duration while they wait in the client buffer!
                .PrefetchCount(100)
                .MaximumMessagesToReceive(100)
                .BufferedInMemory();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task configure_buffered_listener()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            #region sample_buffered_in_memory
            // I overrode the buffering limits just to show
            // that they exist for "back pressure"
            opts.ListenToAzureServiceBusQueue("incoming")
                .BufferedInMemory(new BufferingLimits(1000, 200));

            #endregion


            #region sample_all_outgoing_are_durable
            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

            #endregion
        });

        using var host = builder.Build();
        await host.StartAsync();
    }

    public async Task configure_subscription_filter()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            #region sample_configuring_azure_service_bus_subscription_filter
            opts.ListenToAzureServiceBusSubscription(
                    "subscription1",
                    configureSubscriptionRule: rule =>
                    {
                        rule.Filter = new SqlRuleFilter("NOT EXISTS(user.ignore) OR user.ignore NOT LIKE 'true'");
                    })
                .FromTopic("topic1");

            #endregion
        });

        using var host = builder.Build();
        await host.StartAsync();
    }

    public async Task configure_control_queues()
    {
        #region sample_enabling_azure_service_bus_control_queues
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString)
                .AutoProvision()
                
                // This enables Wolverine to use temporary Azure Service Bus
                // queues created at runtime for communication between
                // Wolverine nodes
                .EnableWolverineControlQueues();


        });

        #endregion

        using var host = builder.Build();
        await host.StartAsync();
    }

    public async Task configure_durable_listener()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            #region sample_durable_endpoint
            // I overrode the buffering limits just to show
            // that they exist for "back pressure"
opts.ListenToAzureServiceBusQueue("incoming")
    .UseDurableInbox(new BufferingLimits(1000, 200));

            opts.PublishAllMessages().ToAzureServiceBusQueue("outgoing")
                .UseDurableOutbox();

            #endregion
        });

        using var host = builder.Build();
        await host.StartAsync();
    }

    public async Task publishing_to_queue()
    {
        #region sample_publishing_to_specific_azure_service_bus_queue
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            // Explicitly configure sending messages to a specific queue
            opts.PublishAllMessages().ToAzureServiceBusQueue("outgoing")

                // All the normal Wolverine options you'd expect
                .BufferedInMemory();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task delivery_expiration_rules_per_subscriber()
    {
        #region sample_delivery_expiration_rules_per_subscriber
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            // Explicitly configure a delivery expiration of 5 seconds
            // for a specific Azure Service Bus queue
            opts.PublishMessage<StatusUpdate>().ToAzureServiceBusQueue("transient")

                // If the messages are transient, it's likely that they should not be
                // durably stored, so make things lighter in your system
                .BufferedInMemory()
                .DeliverWithin(5.Seconds());
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task conventional_listener_configuration()
    {
        #region sample_conventional_listener_configuration_for_azure_service_bus
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()
                // Apply default configuration to all Azure Service Bus listeners
                // This can be overridden explicitly by any configuration for specific
                // listening endpoints
                .ConfigureListeners(listener => { listener.UseDurableInbox(new BufferingLimits(500, 100)); });
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task conventional_subscriber_configuration()
    {
        #region sample_conventional_subscriber_configuration_for_azure_service_bus
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()
                // Apply default configuration to all Azure Service Bus subscribers
                // This can be overridden explicitly by any configuration for specific
                // sending/subscribing endpoints
                .ConfigureSenders(sender => sender.UseDurableOutbox());
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task conventional_routing_no_local_routing()
    {
        #region sample_using_conventional_broker_routing_with_local_routing_turned_off
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // Turn *off* the conventional local routing so that
            // the messages that this application handles still go
            // through the external Azure Service Bus broker
            opts.Policies.DisableConventionalLocalRouting();
            
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()
                .UseConventionalRouting();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task conventional_routing()
    {
        #region sample_conventional_routing_for_azure_service_bus
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()
                .UseConventionalRouting(convention =>
                {
                    // Optionally override the default queue naming scheme
                    convention.QueueNameForSender(t => t.Namespace)

                        // Optionally override the default queue naming scheme
                        .QueueNameForListener(t => t.Namespace)

                        // Fine tune the conventionally discovered listeners
                        .ConfigureListeners((listener, builder) =>
                        {
                            var messageType = builder.MessageType;
                            var runtime = builder.Runtime; // Access to basically everything

                            // customize the new queue
                            listener.CircuitBreaker(queue => { });

                            // other options...
                        })

                        // Fine tune the conventionally discovered sending endpoints
                        .ConfigureSending((subscriber, builder) =>
                        {
                            // Similarly, use the message type and/or wolverine runtime
                            // to customize the message sending
                        });
                });
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    #region sample_message_expiration_by_message
    public async Task message_expiration(IMessageBus bus)
    {
        // Disregard the message if it isn't sent and/or processed within 3 seconds from now
        await bus.SendAsync(new StatusUpdate("Okay"), new DeliveryOptions { DeliverWithin = 3.Seconds() });

        // Disregard the message if it isn't sent and/or processed by 3 PM today
        // but watch all the potentially harmful time zone issues in your real code that I'm ignoring here!
        await bus.SendAsync(new StatusUpdate("Okay"), new DeliveryOptions { DeliverBy = DateTime.Today.AddHours(15) });
    }

    #endregion

    public record StatusUpdate(string Status);


    public static async Task custom_mapping()
    {
        #region sample_customized_envelope_mapping
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            // I overrode the buffering limits just to show
            // that they exist for "back pressure"
            opts.ListenToAzureServiceBusQueue("incoming")
                .UseInterop((queue, mapper) =>
                {
                    // Not sure how useful this would be, but we can start from
                    // the baseline Wolverine mapping and just override a few mappings
                    mapper.MapPropertyToHeader(x => x.ContentType!, "OtherTool.ContentType");
                    mapper.MapPropertyToHeader(x => x.CorrelationId!, "OtherTool.CorrelationId");
                    // and more
                    
                    // or a little uglier where you might be mapping and transforming data between
                    // the transport's model and the Wolverine Envelope
                    mapper.MapProperty(x => x.ReplyUri!,
                        (e, msg) => e.ReplyUri = new Uri($"asb://queue/{msg.ReplyTo}"),
                        (e, msg) => msg.ReplyTo = "response");

                    // customize the incoming mapping
                    mapper.MapIncomingProperty(x => x.ReplyUri!,
                        (e, msg) => e.ReplyUri = new Uri($"asb://queue/{msg.ReplyTo}"));
                    
                });

        });

        #endregion

        using var host = builder.Build();
        await host.StartAsync();
    }
    
    public static async Task nservicebus()
    {
        #region sample_opting_into_nservicebus
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            // I overrode the buffering limits just to show
            // that they exist for "back pressure"
            opts.ListenToAzureServiceBusQueue("incoming")
                .UseNServiceBusInterop();
            
            // This facilitates messaging from NServiceBus (or MassTransit) sending as interface
            // types, whereas Wolverine only wants to deal with concrete types
            opts.Policies.RegisterInteropMessageAssembly(typeof(IInterfaceMessage).Assembly);
        });

        #endregion

        using var host = builder.Build();
        await host.StartAsync();
    }

    public async Task configure_inline_dlq()
    {
        #region sample_asb_inline_dlq

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            // Inline endpoints use Azure Service Bus's *native* dead letter
            // subqueue of the source queue. There's no Wolverine inbox, so
            // dead lettering is handled entirely by Azure Service Bus.
            opts.ListenToAzureServiceBusQueue("inline-queue")
                .ProcessInline();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task configure_buffered_dlq()
    {
        #region sample_asb_buffered_dlq

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            // Buffered endpoints move failed messages to a Wolverine-managed
            // dead letter queue. The default name is "wolverine-dead-letter-queue",
            // but you can override it per endpoint.
            opts.ListenToAzureServiceBusQueue("buffered-queue")
                .BufferedInMemory()
                .ConfigureDeadLetterQueue("my-custom-dlq");
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task configure_durable_dlq()
    {
        #region sample_asb_durable_dlq

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            // Durable endpoints behave like buffered endpoints for dead lettering,
            // but add Wolverine's durable inbox persistence for reliability.
            opts.ListenToAzureServiceBusQueue("durable-queue")
                .UseDurableInbox()
                .ConfigureDeadLetterQueue("my-custom-dlq");
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task disable_dlq()
    {
        #region sample_disable_asb_dlq

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

            // Disable Wolverine-managed dead letter queueing for this endpoint.
            // Failed messages fall back to Wolverine's regular error handling.
            opts.ListenToAzureServiceBusQueue("no-dlq")
                .DisableDeadLetterQueueing();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }
}

public interface IInterfaceMessage;
