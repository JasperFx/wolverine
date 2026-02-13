using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class TopicsComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public TopicsComplianceFixture() : base(new Uri("asb://topic/topic1"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision();
        });

        await ReceiverIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision();

            opts.ListenToAzureServiceBusSubscription("subscription1").FromTopic("topic1");
        });
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    protected override Task AfterDisposeAsync()
    {
        return AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }
}

public class TopicAndSubscriptionSendingAndReceivingCompliance : TransportCompliance<TopicsComplianceFixture>;