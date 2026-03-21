using Wolverine.AmazonSns.Internal;
using Wolverine.AmazonSqs;
using Wolverine.ComplianceTests.Compliance;

namespace Wolverine.AmazonSns.Tests;

public class InlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    private static int _number;

    public InlineComplianceFixture() : base(new Uri($"{AmazonSnsTransport.SnsProtocol}://receiver"), 120)
    {
        IsSenderOnlyTransport = true;
    }

    public async Task InitializeAsync()
    {
        var number = ++_number;

        OutboundAddress = new Uri($"{AmazonSnsTransport.SnsProtocol}://receiver-" + number);

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup();
            
            opts.ListenToSqsQueue("receiver-" + number).Named("receiver")
                .ProcessInline().ReceiveSnsTopicMessage();
        });
        
        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("sender-" + number).ReceiveSnsTopicMessage();
            
            opts.UseAmazonSnsTransportLocally()
                .AutoProvision();

            opts.PublishAllMessages()
                .ToSnsTopic("receiver-" + number)
                .SubscribeSqsQueue("receiver-" + number)
                .SendInline();
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

public class InlineSendingAndReceivingCompliance : TransportCompliance<InlineComplianceFixture>
{
}
