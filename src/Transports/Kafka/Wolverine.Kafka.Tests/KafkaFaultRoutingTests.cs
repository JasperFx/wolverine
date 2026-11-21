using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.ComplianceTests.ErrorHandling.Faults;
using Wolverine.ErrorHandling;

namespace Wolverine.Kafka.Tests;

public class KafkaFaultRoutingTests : TransportFaultRoutingCompliance
{
    // Per-test topic name to avoid collisions and stale-broker-state issues.
    private readonly string _faultTopic = $"fault-compliance-{Guid.NewGuid():n}";

    public override async Task<IHost> BuildSenderAsync()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                opts.PublishMessage<Fault<OrderPlaced>>().ToKafkaTopic(_faultTopic);

                opts.OnException<Exception>().MoveToErrorQueue();
                opts.PublishFaultEvents();

                opts.Discovery.IncludeType<AlwaysFailsHandler>();
            }).StartAsync();
    }

    public override async Task<IHost> BuildReceiverAsync(FaultSink sink)
    {
        // GH-5736. The subscriber has to read the fault even when the sender publishes it before the subscriber's
        // brand new consumer group has a position on the topic, which is the normal case here: the fault goes out
        // a moment after the sender starts. Two things made that a flake. The topic was created by the receiver's
        // own AutoProvision just before it subscribed, which on Kafka 4 leaves the listener without a partition
        // until its next heartbeat five seconds later (GH-5676, see KafkaComplianceTopics.CreateAsync). And the
        // group then started at the default auto.offset.reset of Latest -- AFTER the fault -- so the fault was
        // never read and the session timed out. Create the topic up front and read it from the beginning.
        await KafkaComplianceTopics.CreateAsync(_faultTopic);

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                opts.ListenToKafkaTopic(_faultTopic).BeginAtEarliest();

                opts.Services.AddSingleton(sink);
                opts.Discovery.IncludeType<FaultSinkHandler>();
            }).StartAsync();
    }
}
