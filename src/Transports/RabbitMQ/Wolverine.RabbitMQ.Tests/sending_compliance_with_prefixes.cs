using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class PrefixedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public PrefixedComplianceFixture() : base(new Uri("rabbitmq://queue/foo-buffered-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseRabbitMq()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToRabbitQueue("buffered-sender");
        });

        await ReceiverIs(opts =>
        {
            opts.UseRabbitMq()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToRabbitQueue("buffered-receiver");
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
        var queue = runtime.Endpoints.EndpointByName("buffered-receiver")
            .ShouldBeOfType<RabbitMqQueue>();
        queue.EndpointName.ShouldBe("buffered-receiver");
        queue
            .QueueName.ShouldBe("foo-buffered-receiver");
    }

    [Fact]
    public void prefix_was_applied_to_queues_for_the_sender()
    {
        var runtime = theSender.Services.GetRequiredService<IWolverineRuntime>();

        var queue = runtime.Endpoints.EndpointByName("buffered-sender")
            .ShouldBeOfType<RabbitMqQueue>();

        queue.EndpointName.ShouldBe("buffered-sender");
        queue
            .QueueName.ShouldBe("foo-buffered-sender");
    }
}