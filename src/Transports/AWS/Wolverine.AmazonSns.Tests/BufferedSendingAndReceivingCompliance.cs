using Wolverine.AmazonSns.Internal;
using Wolverine.AmazonSqs;
using Wolverine.ComplianceTests.Compliance;

namespace Wolverine.AmazonSns.Tests;

public class BufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public BufferedComplianceFixture() : base(new Uri($"{AmazonSnsTransport.SnsProtocol}://receiver"), 120)
    {
        IsSenderOnlyTransport = true;
    }

    public async Task InitializeAsync()
    {
        var number = Guid.NewGuid().ToString().Replace(".", "-");

        OutboundAddress = new Uri($"{AmazonSnsTransport.SnsProtocol}://receiver-" + number);

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision().AutoPurgeOnStartup();

            opts.ListenToSqsQueue("receiver-" + number).Named("receiver")
                .BufferedInMemory().ReceiveSnsTopicMessage();
        });
        
        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision().AutoPurgeOnStartup();

            opts.ListenToSqsQueue("sender-" + number).ReceiveSnsTopicMessage();;
            
            opts.UseAmazonSnsTransportLocally()
                .AutoProvision();

            opts.PublishAllMessages()
                .ToSnsTopic("receiver-" + number)
                .SubscribeSqsQueue("receiver-" + number);
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

public class BufferedSendingAndReceivingCompliance : TransportCompliance<BufferedComplianceFixture>
{
}
