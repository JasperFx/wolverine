using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-5812: with EnableDeduplication(), one host that both publishes to and listens to the same topic has two
// producers on that topic: the application's publishing sender and the listener's requeue sender. Both used to
// take the producer name {ServiceName}-{TopicName}, and Pulsar refuses a second producer with a name already
// connected to the topic.
[Collection("pulsar")]
public class pulsar_dedup_producer_names_5812
{
    [Fact]
    public async Task a_host_that_publishes_and_requeues_to_the_same_deduplicated_topic_delivers_everything()
    {
        await enableNamespaceDeduplicationAsync();

        var topic = $"persistent://public/default/dedup-names-{Guid.NewGuid():N}";

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.ServiceName = "dedupnames" + Guid.NewGuid().ToString("N")[..8];
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));

            opts.PublishMessage<DedupNamesMessage>().ToPulsarTopic(topic).EnableDeduplication().SendInline();

            opts.ListenToPulsarTopic(topic)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .ProcessInline()
                .BeginAtEarliest();

            opts.OnException<DedupNamesFailure>().Requeue(5);

            opts.Services.AddSingleton<DedupNamesSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<DedupNamesMessageHandler>();
        });

        var sink = host.Services.GetRequiredService<DedupNamesSink>();

        // r1 fails once, so the listener's requeue producer sends a copy of it
        await host.SendAsync(new DedupNamesMessage { Id = "r1", FailTimes = 1 });
        await waitForAsync(() => sink.Succeeded.Contains("r1"), sink);

        // and the application keeps publishing to the same topic after that
        for (var i = 0; i < 5; i++)
        {
            await host.SendAsync(new DedupNamesMessage { Id = "p" + i });
        }

        // r2 is requeued after the publishing producer has sent messages of its own
        await host.SendAsync(new DedupNamesMessage { Id = "r2", FailTimes = 2 });

        await waitForAsync(() => sink.Succeeded.Count == 7, sink);

        sink.Deliveries["r1"].ShouldBe(2);
        sink.Deliveries["r2"].ShouldBe(3);
        for (var i = 0; i < 5; i++)
        {
            sink.Deliveries["p" + i].ShouldBe(1);
        }
    }

    private static async Task waitForAsync(Func<bool> condition, DedupNamesSink sink)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Not every message was handled. Deliveries: {string.Join(", ", sink.Deliveries.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"))}; succeeded: {string.Join(", ", sink.Succeeded.Order())}");
    }

    private static async Task enableNamespaceDeduplicationAsync()
    {
        using var http = new HttpClient();
        var url = $"{PulsarContainerFixture.HttpServiceUrl}/admin/v2/namespaces/public/default/deduplication";
        using var content = new StringContent("true", Encoding.UTF8, "application/json");
        var response = await http.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        await Task.Delay(1.Seconds());
    }
}

// GH-5812, deterministic: the producer names each sender asks the client for, with a substituted Pulsar client
public class pulsar_dedup_producer_names_deterministic_5812
{
    private const string Primary = "persistent://public/default/dedup-names-5812";
    private const string Other = "persistent://public/default/dedup-names-5812-other";

    [Fact]
    public async Task the_requeue_producer_and_the_publishing_producer_have_different_names()
    {
        var names = new ProducerNames();
        var endpoint = names.Endpoint(e => e.DeduplicationEnabled = true);

        await using var publisher = new PulsarSender(names.Runtime, endpoint, names.Transport, CancellationToken.None);
        await using var listener = names.BuildListener(endpoint);

        names.On(Primary).Count.ShouldBe(2);
        names.On(Primary).Distinct().Count().ShouldBe(2);
        names.On(Primary).ShouldContain("dedupnames5812-dedup-names-5812");
    }

    [Fact]
    public async Task an_explicit_producer_name_is_kept_for_publishing_and_suffixed_for_requeue()
    {
        var names = new ProducerNames();
        var endpoint = names.Endpoint(e =>
        {
            e.DeduplicationEnabled = true;
            e.ProducerName = "orders-producer";
        });

        await using var publisher = new PulsarSender(names.Runtime, endpoint, names.Transport, CancellationToken.None);
        await using var listener = names.BuildListener(endpoint);

        names.On(Primary).ShouldBe(["orders-producer", "orders-producer-requeue"], ignoreOrder: true);
    }

    [Fact]
    public async Task the_requeue_producer_name_is_stable_across_listener_restarts()
    {
        var names = new ProducerNames();
        var endpoint = names.Endpoint(e => e.DeduplicationEnabled = true);

        await using (names.BuildListener(endpoint))
        {
        }

        await using (names.BuildListener(endpoint))
        {
        }

        names.On(Primary).Count.ShouldBe(2);
        names.On(Primary).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task a_requeue_producer_on_another_topic_of_a_multi_topic_listener_does_not_take_its_publishers_name()
    {
        var names = new ProducerNames();
        var endpoint = names.Endpoint(e =>
        {
            e.DeduplicationEnabled = true;
            e.AdditionalTopics.Add(Other);
        });

        // Publishing to the other topic from the same host
        var otherEndpoint = names.Transport.EndpointFor(Other);
        otherEndpoint.DeduplicationEnabled = true;
        await using var publisher = new PulsarSender(names.Runtime, otherEndpoint, names.Transport, CancellationToken.None);

        await using var listener = names.BuildListener(endpoint);
        await listener.DeferAsync(await names.ReceiveAsync(topic: Other));

        names.On(Other).Count.ShouldBe(2);
        names.On(Other).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task without_deduplication_no_producer_is_named()
    {
        var names = new ProducerNames();
        var endpoint = names.Endpoint();

        await using var publisher = new PulsarSender(names.Runtime, endpoint, names.Transport, CancellationToken.None);
        await using var listener = names.BuildListener(endpoint);

        names.On(Primary).ShouldAllBe(x => x == null);
    }

    private sealed class ProducerNames
    {
        private readonly List<(string Topic, string? Name)> _producers = new();
        private readonly Channel<IMessage<ReadOnlySequence<byte>>> _incoming =
            Channel.CreateUnbounded<IMessage<ReadOnlySequence<byte>>>();
        private readonly Channel<Envelope> _received = Channel.CreateUnbounded<Envelope>();

        public ProducerNames()
        {
            var client = Substitute.For<IPulsarClient>();
            client.CreateProducer(Arg.Any<ProducerOptions<ReadOnlySequence<byte>>>())
                .Returns(ci =>
                {
                    var options = ci.Arg<ProducerOptions<ReadOnlySequence<byte>>>();
                    lock (_producers)
                    {
                        _producers.Add((options.Topic, options.ProducerName));
                    }

                    var producer = Substitute.For<IProducer<ReadOnlySequence<byte>>>();
                    producer.Topic.Returns(options.Topic);
                    producer.Send(Arg.Any<MessageMetadata>(), Arg.Any<ReadOnlySequence<byte>>(),
                            Arg.Any<CancellationToken>())
                        .Returns(new ValueTask<MessageId>(MessageId.Earliest));
                    return producer;
                });

            client.CreateConsumer(Arg.Any<ConsumerOptions<ReadOnlySequence<byte>>>())
                .Returns(_ =>
                {
                    var consumer = Substitute.For<IConsumer<ReadOnlySequence<byte>>>();
                    consumer.Topic.Returns(Primary);
                    consumer.Receive(Arg.Any<CancellationToken>())
                        .Returns(ci => new ValueTask<IMessage<ReadOnlySequence<byte>>>(
                            _incoming.Reader.ReadAsync(ci.Arg<CancellationToken>()).AsTask()));
                    return consumer;
                });

            Transport.Client = client;

            Runtime = Substitute.For<IWolverineRuntime>();
            Runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
            Runtime.Options.Returns(new WolverineOptions { ServiceName = "dedupnames5812" });
        }

        public PulsarTransport Transport { get; } = new();
        public IWolverineRuntime Runtime { get; }

        public PulsarEndpoint Endpoint(Action<PulsarEndpoint>? configure = null)
        {
            var endpoint = Transport.EndpointFor(Primary);
            endpoint.Mode = EndpointMode.Inline;
            configure?.Invoke(endpoint);
            return endpoint;
        }

        public PulsarListener BuildListener(PulsarEndpoint endpoint)
        {
            var receiver = Substitute.For<IReceiver>();
            receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>())
                .Returns(ci =>
                {
                    _received.Writer.TryWrite(ci.Arg<Envelope>());
                    return ValueTask.CompletedTask;
                });

            return new PulsarListener(Runtime, endpoint, receiver, Transport, CancellationToken.None);
        }

        public async Task<Envelope> ReceiveAsync(string topic)
        {
            var message = Substitute.For<IMessage<ReadOnlySequence<byte>>>();
            message.MessageId.Returns(new MessageId(10, 1, -1, -1, topic));
            message.Properties.Returns(new Dictionary<string, string>
            {
                [EnvelopeConstants.IdKey] = Guid.NewGuid().ToString(),
                [EnvelopeConstants.MessageTypeKey] = "dedup-names-5812",
                [EnvelopeConstants.ContentTypeKey] = "application/json"
            });
            message.Data.Returns(new ReadOnlySequence<byte>("{}"u8.ToArray()));

            await _incoming.Writer.WriteAsync(message);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _received.Reader.ReadAsync(timeout.Token);
        }

        public List<string?> On(string topic)
        {
            lock (_producers)
            {
                return _producers.Where(x => x.Topic == topic).Select(x => x.Name).ToList();
            }
        }
    }
}

public class DedupNamesFailure : Exception;

public class DedupNamesSink
{
    public ConcurrentDictionary<string, int> Deliveries { get; } = new();
    public ConcurrentBag<string> Succeeded { get; } = new();
}

public class DedupNamesMessage
{
    public string Id { get; set; } = string.Empty;
    public int FailTimes { get; set; }
}

public class DedupNamesMessageHandler
{
    public static void Handle(DedupNamesMessage message, DedupNamesSink sink)
    {
        if (sink.Deliveries.AddOrUpdate(message.Id, 1, (_, c) => c + 1) <= message.FailTimes)
        {
            throw new DedupNamesFailure();
        }

        sink.Succeeded.Add(message.Id);
    }
}
