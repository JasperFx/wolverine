using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TestingSupport.Compliance;
using Wolverine.Runtime;
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
        });

        await ReceiverIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToAzureServiceBusQueue("buffered-receiver").BufferedInMemory();
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("acceptance")]
public class BufferedSendingAndReceivingCompliance : TransportCompliance<BufferedComplianceFixture>
{
    [Fact]
    public virtual async Task dlq_mechanics()
    {
        throwOnAttempt<DivideByZeroException>(1);
        throwOnAttempt<DivideByZeroException>(2);
        throwOnAttempt<DivideByZeroException>(3);

        await shouldMoveToErrorQueueOnAttempt(1);

        var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();

        var transport = runtime.Options.Transports.GetOrCreate<AzureServiceBusTransport>();
        var queue = transport.Queues[AzureServiceBusTransport.DeadLetterQueueName];
        await queue.InitializeAsync(NullLogger.Instance);
        
        var messageReceiver = transport.BusClient.CreateReceiver(AzureServiceBusTransport.DeadLetterQueueName);
        var queued = await messageReceiver.ReceiveMessageAsync();
        queued.ShouldNotBeNull();

    }
}