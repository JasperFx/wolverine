using DotPulsar;
using DotPulsar.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Transports.Sending;

namespace Wolverine.Pulsar.Tests;

public static class DocumentationSamples
{
    public static async Task configure()
    {
        #region sample_configuring_pulsar
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.UsePulsar(c =>
            {
                var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
                c.ServiceUrl(pulsarUri!);
                
                // Any other configuration you want to apply to your
                // Pulsar client
            });

            // Publish messages to a particular Pulsar topic
            opts.PublishMessage<Message1>()
                .ToPulsarTopic("persistent://public/default/one")
                
                // And all the normal Wolverine options...
                .SendInline();

            // Listen for incoming messages from a Pulsar topic
            opts.ListenToPulsarTopic("persistent://public/default/two")
                .SubscriptionName("two")
                .SubscriptionType(SubscriptionType.Exclusive)
                
                // And all the normal Wolverine options...
                .Sequential();


            // Listen for incoming messages from a Pulsar topic with a shared subscription and using RETRY and DLQ queues
            opts.ListenToPulsarTopic("persistent://public/default/three")
                .WithSharedSubscriptionType()
                .DeadLetterQueueing(new DeadLetterTopic(DeadLetterTopicMode.Native))
                .RetryLetterQueueing(new RetryLetterTopic([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)]))
                .Sequential();
        });

        #endregion
    }

    public static async Task disable_requeues()
    {
        #region sample_disable_requeue_for_pulsar
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.UsePulsar(c =>
            {
                var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
                c.ServiceUrl(pulsarUri!);
            });

            // Listen for incoming messages from a Pulsar topic
            opts.ListenToPulsarTopic("persistent://public/default/two")
                .SubscriptionName("two")
                .SubscriptionType(SubscriptionType.Exclusive)
                
                // Disable the requeue for this topic
                .DisableRequeue()
                
                // And all the normal Wolverine options...
                .Sequential();

            // Disable requeue for all Pulsar endpoints
            opts.DisablePulsarRequeue();
        });

        #endregion
    }

    public static async Task policy_configuration()
    {
        #region sample_pulsar_unsubscribe_on_close
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.UsePulsar(c =>
            {
                var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
                c.ServiceUrl(pulsarUri!);
            });

            // Disable unsubscribe on close for all Pulsar endpoints
            opts.UnsubscribePulsarOnClose(PulsarUnsubscribeOnClose.Disabled);
        });

        #endregion
    }

    public static async Task named_brokers()
    {
        #region sample_pulsar_named_broker
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The default Pulsar broker
                opts.UsePulsar(c => c.ServiceUrl(new Uri("pulsar://localhost:6650")));

                // An additional, independent Pulsar broker identified by name. The name doubles as the
                // URI scheme of the named broker's endpoints (e.g. "secondary://..."), so its endpoints
                // never collide with the default "pulsar://" broker.
                opts.AddNamedPulsarBroker(new BrokerName("secondary"),
                    c => c.ServiceUrl(new Uri("pulsar://secondary-pulsar:6650")));

                // Publish a message type to a topic on the named broker
                opts.PublishMessage<Message1>()
                    .ToPulsarTopicOnNamedBroker(new BrokerName("secondary"),
                        "persistent://public/default/one");

                // Listen to a topic on the named broker
                opts.ListenToPulsarTopicOnNamedBroker(new BrokerName("secondary"),
                    "persistent://public/default/two");
            }).StartAsync();
        #endregion
    }

    public static async Task broker_per_tenant()
    {
        #region sample_pulsar_broker_per_tenant
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePulsar(c => c.ServiceUrl(new Uri("pulsar://localhost:6650")))

                    // Route messages with an unknown/missing tenant id to the default broker above
                    .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

                    // Each tenant is served by its own dedicated Pulsar CLUSTER. This is NOT the native
                    // Pulsar tenant segment in persistent://{tenant}/{namespace}/{topic} — the differentiator
                    // is the cluster connection (service URL + auth), and the native tenant/namespace stays
                    // whatever the shared topic topology declares.
                    .AddTenant("tenant-a", new Uri("pulsar://tenant-a-pulsar:6650"))

                    // The action overload runs against a fresh PulsarClient.Builder(), so it must FULLY
                    // specify the tenant cluster's ServiceUrl and any authentication.
                    .AddTenant("tenant-b", client =>
                    {
                        client.ServiceUrl(new Uri("pulsar://tenant-b-pulsar:6650"));
                        // client.Authentication(...); // tenant-specific auth if needed
                    });

                // One shared topic topology; the cluster is chosen at runtime from each message's tenant id
                opts.PublishMessage<Message1>().ToPulsarTopic("persistent://public/default/one");
                opts.ListenToPulsarTopic("persistent://public/default/one");
            }).StartAsync();
        #endregion
    }
}
