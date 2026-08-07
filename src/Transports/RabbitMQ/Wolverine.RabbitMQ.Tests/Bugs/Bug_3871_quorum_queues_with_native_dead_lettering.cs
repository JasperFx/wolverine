using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

// GH-3871. The shared dead letter queue endpoint is created lazily inside
// RabbitMqTransport.tryBuildSystemEndpoints(), which BrokerTransport.InitializeEndpointsAsync() runs
// *after* its Compile() loop -- so the DLQ endpoint was the one endpoint in the transport that had
// never had the endpoint policies applied to it. Resource setup declares whatever it finds there
// without compiling anything, so with AddResourceSetupOnStartup() the DLQ was declared classic and
// then redeclared as quorum at runtime start up. Rabbit MQ answers the second declaration with a
// channel-level 406 (queue type is immutable), which kills the channel, and the listener then died
// on an ObjectDisposedException from BasicQosAsync with no trace of the real cause.
public class Bug_3871_quorum_queues_with_native_dead_lettering
{
    [Fact]
    public async Task quorum_queues_and_native_dead_lettering_declare_consistently()
    {
        // A DLQ name no other test or run has declared: the failure is a *mismatch* between two
        // declarations, so a queue left over as either type would mask it.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var queueName = "orders_" + suffix;
        var dlqName = "dlq_" + suffix;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRabbitMq()
                    .CustomizeDeadLetterQueueing(new DeadLetterQueue(dlqName))
                    .UseQuorumQueues()
                    .AutoProvision();

                opts.ListenToRabbitQueue(queueName);

                // The missing ingredient in the original report. Without resource setup the DLQ
                // endpoint is only ever declared from BrokerTransport.startupAsync(), which compiles
                // each endpoint immediately before declaring it, so the mismatch never appears.
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<RabbitMqTransport>();

        // UseQuorumQueues() applies to every Application role queue, and the shared DLQ is one, so
        // the DLQ has to be quorum in both declarations rather than classic in the first.
        transport.Queues[dlqName].QueueType.ShouldBe(QueueType.quorum);
        transport.Queues[queueName].QueueType.ShouldBe(QueueType.quorum);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
