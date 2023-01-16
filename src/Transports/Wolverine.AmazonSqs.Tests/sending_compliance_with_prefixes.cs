using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TestingSupport.Compliance;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Tests;

public class PrefixedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public PrefixedComplianceFixture() : base(new Uri("sqs://foo-buffered-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("buffered-sender");
        });

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("buffered-receiver");
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
        runtime.Endpoints.EndpointByName("buffered-receiver")
            .ShouldBeOfType<AmazonSqsQueue>()
            .QueueName.ShouldBe("foo-buffered-receiver");
    }

    [Fact]
    public void prefix_was_applied_to_queues_for_the_sender()
    {
        var runtime = theSender.Services.GetRequiredService<IWolverineRuntime>();

        runtime.Endpoints.EndpointByName("buffered-sender")
            .ShouldBeOfType<AmazonSqsQueue>()
            .QueueName.ShouldBe("foo-buffered-sender");
    }
}