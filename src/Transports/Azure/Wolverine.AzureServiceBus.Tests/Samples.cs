using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using TestingSupport.Compliance;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Util;

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