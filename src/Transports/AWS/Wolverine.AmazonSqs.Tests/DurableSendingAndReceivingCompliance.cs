using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using JasperFx.Resources;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Marten;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Tests;

public class DurableComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public DurableComplianceFixture() : base(new Uri("sqs://receiver"), 120)
    {
        ExpectsTransportHealthCheck = true;
    }

    public async ValueTask InitializeAsync()
    {
        // GH-5736: names of this fixture's own. A per-class counter starting at 1 gave the Inline and Durable fixtures
        // the same receiver-1/sender-1 queues, and the next process on the same LocalStack did too. A disposed host's
        // receive loop leaves its long poll open at the broker for up to WaitTimeSeconds, and the broker hands the next
        // message sent to that queue to the dead poll, where it sits invisible for the visibility timeout
        var number = Guid.NewGuid().ToString("N");

        OutboundAddress = new Uri("sqs://receiver-" + number);

        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureListeners(x => x.UseDurableInbox());

            opts.Services.AddMarten(store =>
            {
                store.Connection(Servers.PostgresConnectionString);
                store.DatabaseSchemaName = "sender";
            }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "sender");

            opts.Services.AddResourceSetupOnStartup();

            opts.ListenToSqsQueue("sender-" + number);
        });

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureListeners(x => x.UseDurableInbox());

            opts.Services.AddMarten(store =>
            {
                store.Connection(Servers.PostgresConnectionString);
                store.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "receiver");

            opts.Services.AddResourceSetupOnStartup();

            opts.ListenToSqsQueue("receiver-" + number);
        });

        await Sender.RebuildAllEnvelopeStorageAsync();
        await Receiver.RebuildAllEnvelopeStorageAsync();
    }


    public class DurableSendingAndReceivingCompliance : TransportCompliance<DurableComplianceFixture>
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
            var messages = await transport.Client!.ReceiveMessageAsync(queue.QueueUrl, TestContext.Current.CancellationToken);
            messages.Messages.Count.ShouldBeGreaterThan(0);
        }
    }
}