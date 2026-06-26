using System.Buffers;
using System.Collections.Concurrent;
using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3180: acknowledgment-strategy choice (individual / cumulative / batched).
public class acknowledgment_strategy
{
    private static MessageId Id(ulong entryId) => new(0UL, entryId, -1, -1, "");

    private static (IConsumer<ReadOnlySequence<byte>> consumer, List<MessageId> cumulative,
        List<List<MessageId>> batches, List<MessageId> individual) fakeConsumer()
    {
        var consumer = Substitute.For<IConsumer<ReadOnlySequence<byte>>>();
        var cumulative = new List<MessageId>();
        var batches = new List<List<MessageId>>();
        var individual = new List<MessageId>();

        consumer.AcknowledgeCumulative(Arg.Any<MessageId>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
        consumer.When(c => c.AcknowledgeCumulative(Arg.Any<MessageId>(), Arg.Any<CancellationToken>()))
            .Do(ci => cumulative.Add(ci.Arg<MessageId>()));

        consumer.Acknowledge(Arg.Any<IEnumerable<MessageId>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
        consumer.When(c => c.Acknowledge(Arg.Any<IEnumerable<MessageId>>(), Arg.Any<CancellationToken>()))
            .Do(ci => batches.Add(ci.Arg<IEnumerable<MessageId>>().ToList()));

        consumer.Acknowledge(Arg.Any<MessageId>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
        consumer.When(c => c.Acknowledge(Arg.Any<MessageId>(), Arg.Any<CancellationToken>()))
            .Do(ci => individual.Add(ci.Arg<MessageId>()));

        return (consumer, cumulative, batches, individual);
    }

    // ---- config unit tests ----

    [Fact]
    public void config_sets_each_strategy()
    {
        PulsarEndpoint apply(Action<PulsarListenerConfiguration> configure)
        {
            var endpoint = new PulsarTransport()[new Uri("pulsar://persistent/public/default/ack")];
            var config = new PulsarListenerConfiguration(endpoint);
            configure(config);
            ((IDelayedEndpointConfiguration)config).Apply();
            return endpoint;
        }

        apply(c => { }).AckStrategy.ShouldBe(PulsarAckStrategy.Individual);
        apply(c => c.AcknowledgeCumulative()).AckStrategy.ShouldBe(PulsarAckStrategy.Cumulative);

        var batched = apply(c => c.AcknowledgeInBatches(50, 2.Seconds()));
        batched.AckStrategy.ShouldBe(PulsarAckStrategy.Batched);
        batched.AckBatchSize.ShouldBe(50);
        batched.AckBatchInterval.ShouldBe(2.Seconds());
    }

    // ---- handler logic (deterministic, no broker) ----

    [Fact]
    public async Task individual_acks_each_message()
    {
        var (consumer, _, _, individual) = fakeConsumer();
        var handler = new PulsarAckHandler(consumer, PulsarAckStrategy.Individual, 100, TimeSpan.Zero, default);

        await handler.CompleteAsync(Id(1));
        await handler.CompleteAsync(Id(2));

        individual.Select(x => x.EntryId).ShouldBe([1UL, 2UL]);
    }

    [Fact]
    public async Task batched_flushes_by_count()
    {
        var (consumer, _, batches, _) = fakeConsumer();
        var handler = new PulsarAckHandler(consumer, PulsarAckStrategy.Batched, 3, TimeSpan.Zero, default);

        await handler.CompleteAsync(Id(1));
        await handler.CompleteAsync(Id(2));
        batches.ShouldBeEmpty();

        await handler.CompleteAsync(Id(3));
        batches.ShouldHaveSingleItem();
        batches[0].Select(x => x.EntryId).ShouldBe([1UL, 2UL, 3UL]);
    }

    [Fact]
    public async Task cumulative_never_acks_past_an_in_flight_message()
    {
        var (consumer, cumulative, _, _) = fakeConsumer();
        var handler = new PulsarAckHandler(consumer, PulsarAckStrategy.Cumulative, 100, TimeSpan.Zero, default);

        var m1 = Id(1);
        var m2 = Id(2);
        var m3 = Id(3);
        handler.Track(m1);
        handler.Track(m2);
        handler.Track(m3);

        // Out-of-order completion: 2 and 3 finish while 1 is still in flight. Cumulative ack must NOT
        // advance, because doing so would confirm the still-in-flight message 1.
        await handler.CompleteAsync(m2);
        cumulative.ShouldBeEmpty();
        await handler.CompleteAsync(m3);
        cumulative.ShouldBeEmpty();

        // Now the earliest in-flight message completes: the contiguous prefix is 1,2,3, so a single
        // cumulative ack advances straight to 3.
        await handler.CompleteAsync(m1);
        cumulative.ShouldHaveSingleItem();
        cumulative[0].EntryId.ShouldBe(3UL);
    }

    [Fact]
    public async Task cumulative_advances_in_order()
    {
        var (consumer, cumulative, _, _) = fakeConsumer();
        var handler = new PulsarAckHandler(consumer, PulsarAckStrategy.Cumulative, 100, TimeSpan.Zero, default);

        var m1 = Id(1);
        var m2 = Id(2);
        handler.Track(m1);
        handler.Track(m2);

        await handler.CompleteAsync(m1);
        await handler.CompleteAsync(m2);

        cumulative.Select(x => x.EntryId).ShouldBe([1UL, 2UL]);
    }

    // ---- startup validation ----

    [Fact]
    public async Task cumulative_on_a_shared_subscription_is_rejected_at_startup()
    {
        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var _ = await WolverineHost.ForAsync(opts =>
            {
                opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
                opts.ListenToPulsarTopic($"persistent://public/default/ackshared-{Guid.NewGuid():N}")
                    .SubscriptionType(SubscriptionType.Shared)
                    .AcknowledgeCumulative();
            });
        });

        ex.ToString().ShouldContain("Cumulative");
    }

    // ---- end-to-end ----

    [Fact]
    public async Task batched_acknowledgment_delivers_all_messages()
    {
        var topic = $"persistent://public/default/ackbatch-{Guid.NewGuid():N}";

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishMessage<AckMessage>().ToPulsarTopic(topic).SendInline();
            opts.ListenToPulsarTopic(topic)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .AcknowledgeInBatches(3, 500.Milliseconds());
            opts.Services.AddSingleton<AckSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<AckHandler>();
        });

        for (var i = 0; i < 5; i++)
        {
            await host.SendAsync(new AckMessage { Id = "m-" + i });
        }

        var sink = host.Services.GetRequiredService<AckSink>();
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < cutoff && sink.Received.Count < 5)
        {
            await Task.Delay(100);
        }

        sink.Received.OrderBy(x => x).ShouldBe(["m-0", "m-1", "m-2", "m-3", "m-4"]);
    }
}

public class AckMessage
{
    public string Id { get; set; } = string.Empty;
}

public class AckSink
{
    public ConcurrentBag<string> Received { get; } = new();
}

public class AckHandler
{
    public static void Handle(AckMessage message, AckSink sink) => sink.Received.Add(message.Id);
}
