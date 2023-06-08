using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TestingSupport.Compliance;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Tests;

public class PrefixedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public static int Number = 0;
    
    public PrefixedComplianceFixture() : base(new Uri("sqs://foo-buffered-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var number = ++Number;
        OutboundAddress = new Uri("sqs://foo-prefix-receiver-" + number);
        
        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransport()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("prefix-sender-" + number).Named("prefix-sender");
        });

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransport()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("prefix-receiver-" + number).Named("prefix-receiver");
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("acceptance")]
public class PrefixedSendingAndReceivingCompliance : TransportCompliance<PrefixedComplianceFixture>
{
    [Fact]
    public void prefix_was_applied_to_queues_for_the_receiver()
    {
        var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Endpoints.EndpointByName("prefix-receiver")
            .ShouldBeOfType<AmazonSqsQueue>()
            .QueueName.ShouldStartWith("foo-prefix-receiver");
    }

    [Fact]
    public void prefix_was_applied_to_queues_for_the_sender()
    {
        var runtime = theSender.Services.GetRequiredService<IWolverineRuntime>();

        runtime.Endpoints.EndpointByName("prefix-sender")
            .ShouldBeOfType<AmazonSqsQueue>()
            .QueueName.ShouldStartWith("foo-prefix-sender");
    }
}