using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class InlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public InlineComplianceFixture() : base(new Uri("asb://queue/inline-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var queueName = Guid.NewGuid().ToString();
        OutboundAddress = new Uri("asb://queue/" + queueName);

        await SenderIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision();
        });

        await ReceiverIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision();

            #region sample_using_process_inline

            // Configuring a Wolverine application to listen to
            // an Azure Service Bus queue with the "Inline" mode
            opts.ListenToAzureServiceBusQueue(queueName, q => q.Options.AutoDeleteOnIdle = 5.Minutes()).ProcessInline();

            #endregion
        });
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

[Collection("acceptance")]
public class InlineSendingAndReceivingCompliance : TransportCompliance<InlineComplianceFixture>;