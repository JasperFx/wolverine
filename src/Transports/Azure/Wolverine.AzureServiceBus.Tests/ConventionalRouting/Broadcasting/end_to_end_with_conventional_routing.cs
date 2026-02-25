using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

public class end_to_end_with_conventional_routing : IAsyncLifetime
{
    private IHost _receiver;
    private IHost _sender;

    public Task InitializeAsync()
    {
        _sender = WolverineHost.For(opts =>
        {
            opts.UseAzureServiceBusTesting().UseTopicAndSubscriptionConventionalRouting(x =>
            {
                // Can't use the full name because of limitations on name length
                x.SubscriptionNameForListener(t => t.Name.ToLowerInvariant());
                x.TopicNameForListener(t => t.Name.ToLowerInvariant());
                x.TopicNameForSender(t => t.Name.ToLowerInvariant());
            }).AutoProvision().AutoPurgeOnStartup();
            opts.DisableConventionalDiscovery();
            opts.ServiceName = "Sender";
        });

        _receiver = WolverineHost.For(opts =>
        {
            #region sample_using_topic_and_subscription_conventional_routing_with_azure_service_bus

            opts.UseAzureServiceBusTesting()
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

            opts.ServiceName = "Receiver";
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_sender != null) await _sender.StopAsync();
        if (_receiver != null) await _receiver.StopAsync();
        _sender?.Dispose();
        _receiver?.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public async Task send_from_one_node_to_another_all_with_conventional_routing()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new Routed2Message());

        var received = session
            .AllRecordsInOrder()
            .Where(x => x.Envelope.Message?.GetType() == typeof(Routed2Message))
            .Single(x => x.MessageEventType == MessageEventType.Received);

        received
            .ServiceName.ShouldBe("Receiver");
    }
}