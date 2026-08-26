using System.Buffers;
using System.Collections.Concurrent;
using DotPulsar;
using DotPulsar.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-4100. The Pulsar receiving loops used to be bare `Task.Run(async () => { await foreach ... })`
// with no exception handling anywhere inside them. Anything that threw between pulling a message off
// the consumer and handing it to the receiver -- an envelope mapper, a schema codec, the receiver
// itself -- faulted that task, and nothing ever observed it. The consumer stayed connected, the
// health probe still reported Connected, and the listener never read another message: a silently
// dead listener, the same failure mode RabbitMQ fixed in #3391.
[Collection("pulsar")]
public class receive_loop_fault_isolation
{
    [Fact]
    public async Task a_throwing_envelope_mapper_does_not_kill_the_listener()
    {
        var topic = $"persistent://public/default/faulting-mapper-{Guid.NewGuid():N}";
        var mapper = new ThrowsOnFirstMessageMapper();

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishMessage<FaultIsolationMessage>().ToPulsarTopic(topic).SendInline();
            opts.ListenToPulsarTopic(topic)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .UseInterop(mapper);

            opts.Services.AddSingleton<FaultIsolationSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<FaultIsolationHandler>();
        });

        var sink = host.Services.GetRequiredService<FaultIsolationSink>();

        // The first message blows up inside the receiving loop, before the handler pipeline ever sees it.
        await host.SendAsync(new FaultIsolationMessage { Id = "poison" });
        await waitForConditionAsync(() => mapper.Attempts >= 1, 30000);

        // The listener has to still be alive for this one.
        await host.SendAsync(new FaultIsolationMessage { Id = "good" });

        await waitForConditionAsync(() => sink.Received.Contains("good"), 30000);

        sink.Received.ShouldContain("good");
    }

    private static async Task waitForConditionAsync(Func<bool> condition, int timeoutMs)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition not met within {timeoutMs}ms");
    }
}

public class FaultIsolationMessage
{
    public string Id { get; set; } = string.Empty;
}

public class FaultIsolationSink
{
    public ConcurrentBag<string> Received { get; } = new();
}

public class FaultIsolationHandler
{
    public void Handle(FaultIsolationMessage message, FaultIsolationSink sink)
    {
        sink.Received.Add(message.Id);
    }
}

// Throws on the very first incoming message and behaves normally afterwards, standing in for any
// mapper / codec / receiver failure on the receiving loop's hot path.
public class ThrowsOnFirstMessageMapper : IPulsarEnvelopeMapper
{
    private readonly PulsarEnvelopeMapper _inner;
    private int _attempts;

    public ThrowsOnFirstMessageMapper()
    {
        var transport = new PulsarTransport();
        var endpoint = transport[new Uri("pulsar://persistent/public/default/faulting-mapper")];
        _inner = new PulsarEnvelopeMapper(endpoint, null!);
    }

    public int Attempts => _attempts;

    public void MapIncomingToEnvelope(Envelope envelope, IMessage<ReadOnlySequence<byte>> incoming)
    {
        if (Interlocked.Increment(ref _attempts) == 1)
        {
            throw new DivideByZeroException("Simulated failure on the Pulsar receiving loop");
        }

        _inner.MapIncomingToEnvelope(envelope, incoming);
    }

    public void MapEnvelopeToOutgoing(Envelope envelope, MessageMetadata outgoing)
    {
        _inner.MapEnvelopeToOutgoing(envelope, outgoing);
    }
}
