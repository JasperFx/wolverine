using Azure.Messaging.ServiceBus.Administration;
using Shouldly;
using TestingSupport.Compliance;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class TopicsWithCustomRuleComplianceFixture()
    : TransportComplianceFixture(new Uri("asb://topic/topic1"), 120), IAsyncLifetime
{
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

            opts.ListenToAzureServiceBusSubscription(
                    "subscription1", 
                    configureSubscriptionRule: rule =>
                    {
                        rule.Filter = new SqlRuleFilter("NOT EXISTS(user.ignore) OR user.ignore NOT LIKE 'true'");
                    })
                .FromTopic("topic1");
        });
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

[Collection("acceptance")]
public class TopicAndSubscriptionWithCustomRuleSendingAndReceivingCompliance : TransportCompliance<TopicsWithCustomRuleComplianceFixture>
{
    [Fact]
    public async Task ignores_message_not_matching_the_filter()
    {
        /*
         * Please note that this test may take a while to run,
         * as it will wait for a message to be processed by the receiver
         * but there should none be incoming because of the subscription
         * filter.
         */

        var session = await theSender.TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(
                c => c.EndpointFor(theOutboundAddress).SendAsync(
                    new Message1(),
                    new DeliveryOptions()
                        .WithHeader("ignore", "true")));
        
        var record = session.FindEnvelopesWithMessageType<Message1>(MessageEventType.MessageSucceeded).SingleOrDefault();
        record.ShouldBeNull();
    }
}