using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Pubsub.Internal;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Pubsub.Tests;

public class DocumentationSamples
{
    public async Task bootstraping()
    {
        #region sample_basic_setup_to_pubsub

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // Let Wolverine create missing topics and subscriptions as necessary
                    .AutoProvision()

                    // Optionally purge all subscriptions on application startup.
                    // Warning though, this is potentially slow
                    .AutoPurgeOnStartup();
            }).StartAsync();

        #endregion
    }

    public async Task for_local_development()
    {
        #region sample_connect_to_pubsub_emulator

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // Tries to use GCP Pub/Sub emulator, as it defaults
                    // to EmulatorDetection.EmulatorOrProduction. But you can
                    // supply your own, like EmulatorDetection.EmulatorOnly
                    .UseEmulatorDetection();
            }).StartAsync();

        #endregion
    }

    public async Task enable_system_endpoints()
    {
        #region sample_enable_system_endpoints_in_pubsub

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")
                    .EnableSystemEndpoints();
            }).StartAsync();

        #endregion
    }

    public async Task configuring_listeners()
    {
        #region sample_listen_to_pubsub_topic

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id");

                opts.ListenToPubsubSubscription("incoming1", "incoming1");

                opts.ListenToPubsubSubscription("incoming2", "incoming2")

                    // You can optimize the throughput by running multiple listeners
                    // in parallel
                    .ListenerCount(5)
                    .ConfigurePubsubSubscription(options =>
                    {
                        // Optionally configure the subscription itself
                        options.DeadLetterPolicy = new DeadLetterPolicy
                        {
                            DeadLetterTopic = "errors",
                            MaxDeliveryAttempts = 5
                        };
                        options.AckDeadlineSeconds = 60;
                        options.RetryPolicy = new RetryPolicy
                        {
                            MinimumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(1)),
                            MaximumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(10))
                        };
                    });
            }).StartAsync();

        #endregion
    }

    public async Task publishing()
    {
        #region sample_subscriber_rules_for_pubsub

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id");

                opts
                    .PublishMessage<Message1>()
                    .ToPubsubTopic("outbound1");

                opts
                    .PublishMessage<Message2>()
                    .ToPubsubTopic("outbound2")
                    .MessageRetentionDuration(10.Minutes());
            }).StartAsync();

        #endregion
    }

    public async Task conventional_routing()
    {
        #region sample_conventional_routing_for_pubsub

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")
                    .UseConventionalRouting(convention =>
                    {
                        // Optionally override the default queue naming scheme
                        convention.TopicNameForSender(t => t.Namespace)

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
            }).StartAsync();

        #endregion
    }

    // public async Task enable_dead_lettering()
    // {
    //     #region sample_enable_wolverine_dead_lettering_for_pubsub
    //
    //     var host = await Host.CreateDefaultBuilder()
    //         .UseWolverine(opts =>
    //         {
    //             opts.UsePubsub("your-project-id")
    //
    //                 // Enable dead lettering for all Wolverine endpoints
    //                 .EnableDeadLettering(
    //                     // Optionally configure how the GCP Pub/Sub dead letter itself
    //                     // is created by Wolverine
    //                     options =>
    //                     {
    //                         options.Topic.MessageRetentionDuration =
    //                             Duration.FromTimeSpan(TimeSpan.FromDays(14));
    //
    //                         options.Subscription.MessageRetentionDuration =
    //                             Duration.FromTimeSpan(TimeSpan.FromDays(14));
    //                     }
    //                 );
    //         }).StartAsync();
    //
    //     #endregion
    // }
    //
    // public async Task overriding_wolverine_dead_lettering()
    // {
    //     #region sample_configuring_wolverine_dead_lettering_for_pubsub
    //
    //     var host = await Host.CreateDefaultBuilder()
    //         .UseWolverine(opts =>
    //         {
    //             opts.UsePubsub("your-project-id")
    //                 .EnableDeadLettering();
    //
    //             // No dead letter queueing
    //             opts.ListenToPubsubSubscription("incoming")
    //                 .DisableDeadLettering();
    //
    //             // Use a different dead letter queue
    //             opts.ListenToPubsubSubscription("important")
    //                 .ConfigureDeadLettering(
    //                     "important_errors",
    //
    //                     // Optionally configure how the dead letter itself
    //                     // is built by Wolverine
    //                     e => { e.TelemetryEnabled = true; }
    //                 );
    //         }).StartAsync();
    //
    //     #endregion
    // }

    public async Task customize_mappers()
    {
        #region sample_configuring_custom_envelope_mapper_for_pubsub

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")
                    .UseConventionalRouting()
                    .ConfigureListeners(l => l.UseInterop(new CustomPubsubMapper()))
                    .ConfigureSenders(s => s.UseInterop(new CustomPubsubMapper()));
            }).StartAsync();

        #endregion
    }
}

#region sample_custom_pubsub_mapper

public class CustomPubsubMapper : IPubsubEnvelopeMapper
{

    public void MapEnvelopeToOutgoing(Envelope envelope, PubsubMessage outgoing)
    {
        outgoing.MessageId = envelope.Id.ToString();
        // much more here...
    }

    public void MapIncomingToEnvelope(Envelope envelope, PubsubMessage incoming)
    {
        // This is necessary, but you can use the default message type configuration
        // within Wolverine to deal with it for you
        envelope.MessageType = "some.type";

        // Maybe you're receiving raw JSON or something?
        envelope.Data = incoming.ToByteArray();
    }
}

#endregion