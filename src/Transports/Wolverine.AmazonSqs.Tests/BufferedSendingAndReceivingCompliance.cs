using Baseline.Dates;
using TestingSupport.Compliance;

namespace Wolverine.AmazonSqs.Tests;

public class BufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public BufferedComplianceFixture() : base(new Uri("sqs://buffered-receiver"))
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("buffered-sender");
        });

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("buffered-receiver");
        });
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

[Collection("acceptance")]
public class BufferedSendingAndReceivingCompliance: TransportCompliance<BufferedComplianceFixture>
{
    
}