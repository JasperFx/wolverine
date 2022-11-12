using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Baseline.Dates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Tests;

public class DocumentationSamples
{
    
    public async Task bootstrapping()
    {
        #region sample_basic_connection_to_azure_service_bus

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // One way or another, you're probably pulling the Azure Service Bus
                // connection string out of configuration
                var azureServiceBusConnectionString = context
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
                opts.UseAzureServiceBus(azureServiceBusConnectionString, azure =>
                {
                    azure.RetryOptions.Mode = ServiceBusRetryMode.Exponential;
                });
            }).StartAsync();

        #endregion
    }


    public async Task configuring_queues()
    {
        #region sample_configuring_azure_service_bus_queues

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // One way or another, you're probably pulling the Azure Service Bus
                // connection string out of configuration
                var azureServiceBusConnectionString = context
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
                    .ConfigureQueue(options => { options.LockDuration = 3.Seconds(); });
            }).StartAsync();

        #endregion
    }

    
    public async Task configuring_a_listener()
    {
        #region sample_configuring_an_azure_service_bus_listener

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // One way or another, you're probably pulling the Azure Service Bus
                // connection string out of configuration
                var azureServiceBusConnectionString = context
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
            }).StartAsync();

        #endregion
    }

    public async Task publishing_to_queue()
    {
        #region sample_publishing_to_specific_azure_service_bus_queue

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // One way or another, you're probably pulling the Azure Service Bus
                // connection string out of configuration
                var azureServiceBusConnectionString = context
                    .Configuration
                    .GetConnectionString("azure-service-bus");

                // Connect to the broker in the simplest possible way
                opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

                // Explicitly configure sending messages to a specific queue
                opts.PublishAllMessages().ToAzureServiceBusQueue("outgoing")

                    // All the normal Wolverine options you'd expect
                    .BufferedInMemory();
            }).StartAsync();

        #endregion
    }

    public async Task conventional_listener_configuration()
    {
        #region sample_conventional_listener_configuration_for_azure_service_bus

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // One way or another, you're probably pulling the Azure Service Bus
                // connection string out of configuration
                var azureServiceBusConnectionString = context
                    .Configuration
                    .GetConnectionString("azure-service-bus");

                // Connect to the broker in the simplest possible way
                opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()
                    // Apply default configuration to all Azure Service Bus listeners
                    // This can be overridden explicitly by any configuration for specific
                    // listening endpoints
                    .ConfigureListeners(listener =>
                    {
                        listener.UseDurableInbox(new BufferingLimits(500, 100));
                    });
            }).StartAsync();

        #endregion

    }

    public async Task conventional_subscriber_configuration()
    {
        #region sample_conventional_subscriber_configuration_for_azure_service_bus

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // One way or another, you're probably pulling the Azure Service Bus
                // connection string out of configuration
                var azureServiceBusConnectionString = context
                    .Configuration
                    .GetConnectionString("azure-service-bus");

                // Connect to the broker in the simplest possible way
                opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()
                    // Apply default configuration to all Azure Service Bus subscribers
                    // This can be overridden explicitly by any configuration for specific
                    // sending/subscribing endpoints
                    .ConfigureSenders(sender => sender.UseDurableOutbox());

            }).StartAsync();

        #endregion

    }
    
    
    public async Task conventional_routing()
    {
        #region sample_conventional_routing_for_azure_service_bus

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // One way or another, you're probably pulling the Azure Service Bus
                // connection string out of configuration
                var azureServiceBusConnectionString = context
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
                        .ConfigureListeners((listener, context) =>
                        {
                            var messageType = context.MessageType;
                            var runtime = context.Runtime; // Access to basically everything

                            // customize the new queue
                            listener.CircuitBreaker(queue => { });

                            // other options...
                        })
                        
                        // Fine tune the conventionally discovered sending endpoints
                        .ConfigureSending((subscriber, context) =>
                        {
                            // Similarly, use the message type and/or wolverine runtime
                            // to customize the message sending
                        });
                    });
            }).StartAsync();

        #endregion

    }
    
}