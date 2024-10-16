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
                .GetConnectionString("azure-service-bus");

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
                .GetConnectionString("azure-service-bus");

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
                .GetConnectionString("azure-service-bus");

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

    public async Task configure_buffered_listener()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus");

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
                .GetConnectionString("azure-service-bus");

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
                .GetConnectionString("azure-service-bus");

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
                .GetConnectionString("azure-service-bus");

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
                .GetConnectionString("azure-service-bus");

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
                .GetConnectionString("azure-service-bus");

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
                .GetConnectionString("azure-service-bus");

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
}