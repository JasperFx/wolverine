using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
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
        var queueName = Guid.NewGuid().ToString();
        OutboundAddress = new Uri("asb://queue/foo." + queueName);

        await SenderIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .PrefixIdentifiers("foo")
                .AutoProvision();

        });

        await ReceiverIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .PrefixIdentifiers("foo")
                .AutoProvision();

            opts.ListenToAzureServiceBusQueue(queueName, q => q.Options.AutoDeleteOnIdle = 5.Minutes()).Named("receiver");
        });
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    protected override Task AfterDisposeAsync()
    {
        return AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }
}

public class PrefixedSendingAndReceivingCompliance : TransportCompliance<PrefixedComplianceFixture>
{
    [Fact]
    public void prefix_was_applied_to_queues_for_the_receiver()
    {
        var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Endpoints.EndpointByName("receiver")
            .ShouldBeOfType<AzureServiceBusQueue>()
            .QueueName.ShouldStartWith("foo.");
    }
}