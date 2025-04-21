using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.AmazonSqs;
using Wolverine.AmazonSqs.Internal;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;

namespace Wolverine.AmazonSns.Tests;

public class PrefixedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public static int Number;

    public PrefixedComplianceFixture() : base(new Uri($"{AmazonSnsTransport.SnsProtocol}://foo-buffered-receiver"), 120)
    {
        IsSenderOnlyTransport = true;
    }

    public async Task InitializeAsync()
    {
        var number = ++Number;
        OutboundAddress = new Uri($"{AmazonSnsTransport.SnsProtocol}://boo-prefix-topic-" + number);

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("prefix-receiver-" + number)
                .Named("prefix-receiver")
                .ReceiveSnsTopicMessage();
        });
        
        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("prefix-sender-" + number).Named("prefix-sender");
            
            opts.UseAmazonSnsTransportLocally()
                .PrefixIdentifiers("boo")
                .AutoProvision();
            
            opts.PublishAllMessages()
                .ToSnsTopic("prefix-topic-" + number)
                .Named("prefix-topic")
                // Subscription doesn't take prefix into account, since the queue's prefix is from the SQS transport
                .SubscribeSqsQueue("foo-prefix-receiver-" + number);
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

public class PrefixedSendingAndReceivingCompliance : TransportCompliance<PrefixedComplianceFixture>
{
    [Fact]
    public void prefix_was_applied_to_topics_for_the_sender()
    {
        var runtime = theSender.Services.GetRequiredService<IWolverineRuntime>();

        runtime.Endpoints.EndpointByName("prefix-topic")
            .ShouldBeOfType<AmazonSnsTopic>()
            .TopicName.ShouldStartWith("boo-prefix-topic");
    }
}
