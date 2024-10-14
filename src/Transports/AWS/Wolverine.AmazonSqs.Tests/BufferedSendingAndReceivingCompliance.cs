using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Tests;

public class BufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public BufferedComplianceFixture() : base(new Uri("sqs://receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var number = Guid.NewGuid().ToString().Replace(".", "-");

        OutboundAddress = new Uri("sqs://receiver-" + number);

        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision().AutoPurgeOnStartup();

            opts.ListenToSqsQueue("sender-" + number);
        });

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision().AutoPurgeOnStartup();

            opts.ListenToSqsQueue("receiver-" + number).Named("receiver").BufferedInMemory();
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

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

        var transport = runtime.Options.Transports.GetOrCreate<AmazonSqsTransport>();
        var queue = transport.Queues[AmazonSqsTransport.DeadLetterQueueName];
        await queue.InitializeAsync(NullLogger.Instance);
        var messages = await transport.Client.ReceiveMessageAsync(queue.QueueUrl);
        messages.Messages.Count.ShouldBeGreaterThan(0);
    }
}