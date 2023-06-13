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

            #region sample_using_process_inline

            // Configuring a Wolverine application to listen to
            // an Azure Service Bus queue with the "Inline" mode
            opts.ListenToAzureServiceBusQueue("inline-receiver").ProcessInline();

            #endregion
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