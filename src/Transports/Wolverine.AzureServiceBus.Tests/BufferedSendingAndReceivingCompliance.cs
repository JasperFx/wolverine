using System;
using System.Threading.Tasks;
using TestingSupport.Compliance;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class BufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public BufferedComplianceFixture() : base(new Uri("asb://queue/buffered-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToAzureServiceBusQueue("buffered-sender");
        });

        await ReceiverIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
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
public class BufferedSendingAndReceivingCompliance: TransportCompliance<BufferedComplianceFixture>
{
    
}