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
    public static int Number;

    public DurableComplianceFixture() : base(new Uri("sqs://receiver"), 120)
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

        await Sender.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.RebuildAsync();
        await Receiver.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.RebuildAsync();
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
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
            var messages = await transport.Client.ReceiveMessageAsync(queue.QueueUrl);
            messages.Messages.Count.ShouldBeGreaterThan(0);
        }
    }
}