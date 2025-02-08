using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class Samples
{
    public static async Task configure_topics()
    {
        #region sample_using_azure_service_bus_subscriptions_and_topics

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string")

                    // If this is part of your configuration, Wolverine will try to create
                    // any missing topics or subscriptions in the configuration at application
                    // start up time
                    .AutoProvision();

                // Publish to a topic
                opts.PublishMessage<Message1>().ToAzureServiceBusTopic("topic1")

                    // Option to configure how the topic would be configured if
                    // built by Wolverine
                    .ConfigureTopic(topic =>
                    {
                        topic.MaxSizeInMegabytes = 100;
                    });


                opts.ListenToAzureServiceBusSubscription("subscription1", subscription =>
                    {
                        // Optionally alter how the subscription is created or configured in Azure Service Bus
                        subscription.DefaultMessageTimeToLive = 5.Minutes();
                    })
                    .FromTopic("topic1", topic =>
                    {
                        // Optionally alter how the topic is created in Azure Service Bus
                        topic.DefaultMessageTimeToLive = 5.Minutes();
                    });
            }).StartAsync();

        #endregion
    }

    public static async Task configure_resources()
    {
        #region sample_resource_setup_with_azure_service_bus

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string");

                // Make sure that all known resources like
                // the Azure Service Bus queues, topics, and subscriptions
                // configured for this application exist at application start up
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_auto_provision()
    {
        #region sample_auto_provision_with_azure_service_bus

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string")

                    // Wolverine will build missing queues, topics, and subscriptions
                    // as necessary at runtime
                    .AutoProvision();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_auto_purge()
    {
        #region sample_auto_purge_with_azure_service_bus

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string")
                    .AutoPurgeOnStartup();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_custom_mappers()
    {
        #region sample_configuring_custom_envelope_mapper_for_azure_service_bus

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string")
                    .UseConventionalRouting()

                    .ConfigureListeners(l => l.InteropWith(new CustomAzureServiceBusMapper()))

                    .ConfigureSenders(s => s.InteropWith(new CustomAzureServiceBusMapper()));
            }).StartAsync();

        #endregion
    }
    
public class multi_tenanted_brokers
{
    [Fact]
    public void show_bootstrapping()
    {
        #region sample_configuring_azure_service_bus_for_multi_tenancy

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

                // This is the default, if there is no tenant id on an outgoing message,
                // use the default broker
                .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

                // Or tell Wolverine instead to just quietly ignore messages sent
                // to unrecognized tenant ids
                .TenantIdBehavior(TenantedIdBehavior.IgnoreUnknownTenants)

                // Or be draconian and make Wolverine assert and throw an exception
                // if an outgoing message does not have a tenant id
                .TenantIdBehavior(TenantedIdBehavior.TenantIdRequired)

                // Add new tenants by registering the tenant id and a separate fully qualified namespace
                // to a different Azure Service Bus connection
                .AddTenantByNamespace("one", builder.Configuration.GetValue<string>("asb_ns_one"))
                .AddTenantByNamespace("two", builder.Configuration.GetValue<string>("asb_ns_two"))
                .AddTenantByNamespace("three", builder.Configuration.GetValue<string>("asb_ns_three"))

                // OR, instead, add tenants by registering the tenant id and a separate connection string
                // to a different Azure Service Bus connection
                .AddTenantByConnectionString("four", builder.Configuration.GetConnectionString("asb_four"))
                .AddTenantByConnectionString("five", builder.Configuration.GetConnectionString("asb_five"))
                .AddTenantByConnectionString("six", builder.Configuration.GetConnectionString("asb_six"));
            
            // This Wolverine application would be listening to a queue
            // named "incoming" on all Azure Service Bus connections, including the default
            opts.ListenToAzureServiceBusQueue("incoming");

            // This Wolverine application would listen to a single queue
            // at the default connection regardless of tenant
            opts.ListenToAzureServiceBusQueue("incoming_global")
                .GlobalListener();
            
            // Likewise, you can override the queue, subscription, and topic behavior
            // to be "global" for all tenants with this syntax:
            opts.PublishMessage<Message1>()
                .ToAzureServiceBusQueue("message1")
                .GlobalSender();

            opts.PublishMessage<Message2>()
                .ToAzureServiceBusTopic("message2")
                .GlobalSender();
        });

        #endregion
    }
}
}

#region sample_custom_azure_service_bus_mapper

public class CustomAzureServiceBusMapper : IAzureServiceBusEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, ServiceBusMessage outgoing)
    {
        outgoing.Body = new BinaryData(envelope.Data);
        if (envelope.DeliverWithin != null)
        {
            outgoing.TimeToLive = envelope.DeliverWithin.Value;
        }
    }

    public void MapIncomingToEnvelope(Envelope envelope, ServiceBusReceivedMessage incoming)
    {
        envelope.Data = incoming.Body.ToArray();

        // You will have to help Wolverine out by either telling Wolverine
        // what the message type is, or by reading the actual message object,
        // or by telling Wolverine separately what the default message type
        // is for a listening endpoint
        envelope.MessageType = typeof(Message1).ToMessageTypeName();
    }

    public IEnumerable<string> AllHeaders()
    {
        yield break;
    }
}

#endregion