using TestingSupport.Compliance;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class InlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public InlineComplianceFixture() : base(new Uri("asb://queue/inline-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision()
                .AutoPurgeOnStartup();
        });

        await ReceiverIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToAzureServiceBusQueue("inline-receiver").ProcessInline();
        });
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

[Collection("acceptance")]
public class InlineSendingAndReceivingCompliance : TransportCompliance<InlineComplianceFixture>
{
}