using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.ComplianceTests.ErrorHandling.Faults;
using Wolverine.ErrorHandling;

namespace Wolverine.Kafka.Tests;

public class KafkaFaultRoutingTests : TransportFaultRoutingCompliance
{
    // Per-test-suite topic name to avoid collisions and stale-broker-state issues.
    private static readonly string FaultTopic = $"fault-compliance-{Guid.NewGuid():n}";

    public override async Task<IHost> BuildSenderAsync()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                opts.PublishMessage<Fault<OrderPlaced>>().ToKafkaTopic(FaultTopic);

                opts.OnException<Exception>().MoveToErrorQueue();
                opts.PublishFaultEvents();

                opts.Discovery.IncludeType<AlwaysFailsHandler>();
            }).StartAsync();
    }

    public override async Task<IHost> BuildReceiverAsync(FaultSink sink)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                opts.ListenToKafkaTopic(FaultTopic);

                opts.Services.AddSingleton(sink);
                opts.Discovery.IncludeType<FaultSinkHandler>();
            }).StartAsync();
    }
}
