using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TestingSupport.Compliance;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Tests;

public class InlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public static int Number = 0;

    public InlineComplianceFixture() : base(new Uri("sqs://buffered-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var number = ++Number;

        OutboundAddress = new Uri("sqs://receiver-" + number);

        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("sender-" + number);

            opts.PublishAllMessages().To(OutboundAddress).SendInline();
        });

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.ListenToSqsQueue("receiver-" + number).Named("receiver").ProcessInline();
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("acceptance")]
public class InlineSendingAndReceivingCompliance : TransportCompliance<InlineComplianceFixture>
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