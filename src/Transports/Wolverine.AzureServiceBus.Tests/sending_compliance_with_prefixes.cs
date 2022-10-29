using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TestingSupport.Compliance;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class PrefixedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public PrefixedComplianceFixture() : base(new Uri("asb://queue/foo.buffered-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToAzureServiceBusQueue("buffered-sender");
        });

        await ReceiverIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .PrefixIdentifiers("foo")
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToAzureServiceBusQueue("buffered-receiver");
        });
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

[Collection("acceptance")]
public class PrefixedSendingAndReceivingCompliance: TransportCompliance<PrefixedComplianceFixture>
{
    [Fact]
    public void prefix_was_applied_to_queues_for_the_receiver()
    {
        var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Endpoints.EndpointByName("buffered-receiver")
            .ShouldBeOfType<AzureServiceBusQueue>()
            .QueueName.ShouldBe("foo.buffered-receiver");

    }
    
    [Fact]
    public void prefix_was_applied_to_queues_for_the_sender()
    {
        var runtime = theSender.Services.GetRequiredService<IWolverineRuntime>();

        runtime.Endpoints.EndpointByName("buffered-sender")
            .ShouldBeOfType<AzureServiceBusQueue>()
            .QueueName.ShouldBe("foo.buffered-sender");
    }
}