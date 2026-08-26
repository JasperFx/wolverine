using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class TopicsWithCustomRuleComplianceFixture
    : TransportComplianceFixture, IAsyncLifetime
{
    public TopicsWithCustomRuleComplianceFixture()
        : base(new Uri("asb://topic/topic1"), 120)
    {
        MustReset = false;
    }

    public async ValueTask InitializeAsync()
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

    protected override Task AfterDisposeAsync()
    {
        return AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }
}

public class TopicAndSubscriptionWithCustomRuleSendingAndReceivingCompliance(
    TopicsWithCustomRuleComplianceFixture fixture)
    : TransportCompliance<TopicsWithCustomRuleComplianceFixture>(fixture),
        IClassFixture<TopicsWithCustomRuleComplianceFixture>
{
    [Fact]
    public async Task ignores_message_not_matching_the_filter()
    {
        // The whole point of this test is that the message never arrives -- the subscription rule
        // filters it out -- so there is no tracked activity to complete and the session is SUPPOSED to
        // time out. GH-4125 stopped DoNotAssertOnExceptionsDetected() from suppressing that assertion
        // by accident, so the expectation is now stated by name.
        var session = await theSender.TrackActivity(15.Seconds())
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .DoNotAssertOnTimeout()
            .ExecuteAndWaitAsync(
                c => c.EndpointFor(theOutboundAddress).SendAsync(
                    new Message1(),
                    new DeliveryOptions()
                        .WithHeader("ignore", "true")));

        var record = session.FindEnvelopesWithMessageType<Message1>(MessageEventType.MessageSucceeded).SingleOrDefault();
        record.ShouldBeNull();
    }
}