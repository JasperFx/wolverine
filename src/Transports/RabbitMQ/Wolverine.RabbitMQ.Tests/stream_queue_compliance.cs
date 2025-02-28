using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;


public class StreamQueueFixture : TransportComplianceFixture, IAsyncLifetime
{
    public StreamQueueFixture() : base($"rabbitmq://queue/stream1".ToUri())
    {
    }

    public async Task InitializeAsync()
    {
        OutboundAddress = $"rabbitmq://queue/stream1".ToUri();

        await SenderIs(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .AutoProvision()
                .DisableDeadLetterQueueing()
                .DeclareQueue("stream1")
                .UseStreamsAsQueues();

            opts.ListenToRabbitQueue("stream2").TelemetryEnabled(false);
        });

        await ReceiverIs(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .DisableDeadLetterQueueing()
                .UseStreamsAsQueues();
            
            opts.ListenToRabbitQueue("stream1").TelemetryEnabled(false);
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

public class stream_queue_compliance : TransportCompliance<StreamQueueFixture>
{
    [Fact]
    public void all_queues_are_declared_as_stream()
    {
        var queues = theSender
            .GetRuntime()
            .Options
            .Transports
            .AllEndpoints()
            .OfType<RabbitMqQueue>()
            .Where(x => x.Role == EndpointRole.Application)
            .ToArray();
        
        queues.Any().ShouldBeTrue();
        foreach (var mqQueue in queues)
        {
            mqQueue.QueueType.ShouldBe(QueueType.stream);
        }
        
    }
}