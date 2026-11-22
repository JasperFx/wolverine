using System.Buffers;
using System.Text;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Exceptions;
using DotPulsar.Extensions;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Pulsar.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-5847: with EnableDeduplication(), every running instance of a service asks for the same producer name on a
// topic. Pulsar allows one connected producer per name, so the second instance was refused (ProducerBusy) and its
// sends hung forever while DotPulsar retried. It now fails with a clear error, and ProducerNamePerInstance() gives
// each instance a name of its own.
public class pulsar_instance_producer_names_5847
{
    private const string Topic = "persistent://public/default/instance-names-5847";

    private static PulsarEndpoint endpoint(Action<PulsarConfiguration>? configure = null)
    {
        var transport = new PulsarTransport();
        configure?.Invoke(new PulsarConfiguration(transport));
        var endpoint = transport.EndpointFor(Topic);
        endpoint.DeduplicationEnabled = true;
        return endpoint;
    }

    [Fact]
    public void the_default_name_is_shared_by_every_instance()
    {
        PulsarSender.ProducerNameFor("orders", endpoint(), forRequeue: false)
            .ShouldBe("orders-instance-names-5847");
    }

    [Fact]
    public void the_instance_id_is_appended_to_the_default_name()
    {
        PulsarSender.ProducerNameFor("orders", endpoint(c => c.ProducerNamePerInstance("node-a")), forRequeue: false)
            .ShouldBe("orders-instance-names-5847-node-a");
    }

    [Fact]
    public void the_instance_id_is_appended_to_an_explicit_name()
    {
        var e = endpoint(c => c.ProducerNamePerInstance("node-a"));
        e.ProducerName = "orders-producer";

        PulsarSender.ProducerNameFor("orders", e, forRequeue: false).ShouldBe("orders-producer-node-a");
    }

    [Fact]
    public void a_requeue_producer_takes_the_instance_id_before_its_suffix()
    {
        PulsarSender.ProducerNameFor("orders", endpoint(c => c.ProducerNamePerInstance("node-a")), forRequeue: true)
            .ShouldBe("orders-instance-names-5847-node-a-requeue");
    }

    [Fact]
    public void the_machine_name_is_the_instance_id_when_none_is_given()
    {
        PulsarSender.ProducerNameFor("orders", endpoint(c => c.ProducerNamePerInstance()), forRequeue: false)
            .ShouldBe($"orders-instance-names-5847-{Environment.MachineName}");
    }

    [Fact]
    public void an_empty_instance_id_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => endpoint(c => c.ProducerNamePerInstance(" ")));
    }

    private static ProducerBusyException busy(string name, string topic) =>
        new($"org.apache.pulsar.broker.service.BrokerServiceException$NamingException: Producer with name '{name}' is already connected to topic '{topic}'");

    [Fact]
    public void a_brief_producer_busy_is_retried()
    {
        var guard = new PulsarProducerNameGuard { Timeout = 30.Seconds() };
        guard.Register("orders-a", Topic);
        var start = DateTimeOffset.UtcNow;

        guard.TryFail(busy("orders-a", Topic), start, out _).ShouldBeFalse();
        guard.TryFail(busy("orders-a", Topic), start.AddSeconds(3), out _).ShouldBeFalse();
        guard.TryFail(busy("orders-a", Topic), start.AddSeconds(29), out _).ShouldBeFalse();
        guard.ErrorFor("orders-a", Topic).ShouldBeNull();
    }

    [Fact]
    public void a_producer_busy_that_outlasts_the_timeout_fails_with_a_clear_error()
    {
        var guard = new PulsarProducerNameGuard { Timeout = 30.Seconds() };
        guard.Register("orders-a", Topic);
        var start = DateTimeOffset.UtcNow;

        guard.TryFail(busy("orders-a", Topic), start, out _).ShouldBeFalse();
        for (var i = 3; i < 30; i += 3)
        {
            guard.TryFail(busy("orders-a", Topic), start.AddSeconds(i), out _).ShouldBeFalse();
        }

        guard.TryFail(busy("orders-a", Topic), start.AddSeconds(30), out var error).ShouldBeTrue();

        error.ShouldNotBeNull();
        error.ProducerName.ShouldBe("orders-a");
        error.Topic.ShouldBe(Topic);
        error.Message.ShouldContain("ProducerNamePerInstance");
        error.Message.ShouldContain("ConfigureProducer");
        guard.ErrorFor("orders-a", Topic).ShouldBeSameAs(error);
    }

    [Fact]
    public void a_new_run_of_producer_busy_after_a_gap_starts_the_clock_again()
    {
        var guard = new PulsarProducerNameGuard { Timeout = 30.Seconds() };
        guard.Register("orders-a", Topic);
        var start = DateTimeOffset.UtcNow;

        guard.TryFail(busy("orders-a", Topic), start, out _).ShouldBeFalse();

        // Connected in between, then refused again much later
        guard.TryFail(busy("orders-a", Topic), start.AddMinutes(5), out _).ShouldBeFalse();
        guard.TryFail(busy("orders-a", Topic), start.AddMinutes(5).AddSeconds(3), out _).ShouldBeFalse();
    }

    [Fact]
    public void a_partition_of_the_registered_topic_counts()
    {
        var guard = new PulsarProducerNameGuard { Timeout = 30.Seconds() };
        guard.Register("orders-a", Topic);
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < 30; i += 3)
        {
            guard.TryFail(busy("orders-a", Topic + "-partition-1"), start.AddSeconds(i), out _).ShouldBeFalse();
        }

        guard.TryFail(busy("orders-a", Topic + "-partition-1"), start.AddSeconds(30), out var error).ShouldBeTrue();
        error!.Topic.ShouldBe(Topic);
    }

    [Fact]
    public void a_producer_wolverine_did_not_name_is_left_to_dotpulsar()
    {
        var guard = new PulsarProducerNameGuard { Timeout = 30.Seconds() };
        guard.Register("orders-a", Topic);
        var start = DateTimeOffset.UtcNow;

        guard.TryFail(busy("someone-else", Topic), start, out _).ShouldBeFalse();
        guard.TryFail(busy("someone-else", Topic), start.AddMinutes(2), out _).ShouldBeFalse();
        guard.TryFail(busy("orders-a", "persistent://public/default/another"), start, out _).ShouldBeFalse();
        guard.TryFail(busy("orders-a", "persistent://public/default/another"), start.AddMinutes(2), out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void other_failures_are_left_to_dotpulsar()
    {
        var guard = new PulsarProducerNameGuard { Timeout = 30.Seconds() };
        guard.Register("orders-a", Topic);
        var start = DateTimeOffset.UtcNow;

        var other = new InvalidOperationException($"Producer with name 'orders-a' is already connected to topic '{Topic}'");
        guard.TryFail(other, start, out _).ShouldBeFalse();
        guard.TryFail(other, start.AddMinutes(2), out _).ShouldBeFalse();
    }
}

// GH-5847 against the broker: two instances of one service publishing to one deduplicated topic
[Collection("pulsar")]
public class pulsar_instance_producer_names_against_the_broker_5847
{
    [Fact]
    public async Task a_second_instance_with_the_shared_name_fails_fast_with_a_clear_error()
    {
        await enableNamespaceDeduplicationAsync();
        var topic = $"persistent://public/default/instance-names-{Guid.NewGuid():N}";
        var serviceName = "instances" + Guid.NewGuid().ToString("N")[..8];

        using var first = await startInstanceAsync(serviceName, null);
        using var second = await startInstanceAsync(serviceName, null);

        await using var firstSender = senderFor(first, topic);
        await firstSender.SendAsync(envelope("a"));

        await using var secondSender = senderFor(second, topic);

        // It used to hang here for as long as the first instance stayed connected
        var error = await Should.ThrowAsync<PulsarProducerNameInUseException>(
            () => secondSender.SendAsync(envelope("b")).AsTask().WaitAsync(45.Seconds(), TestContext.Current.CancellationToken));

        error.Topic.ShouldBe(topic);
        error.ProducerName.ShouldBe($"{serviceName}-{topic.Split('/').Last()}");
        error.Message.ShouldContain("ProducerNamePerInstance");
    }

    [Fact]
    public async Task instances_with_their_own_names_both_publish_and_each_is_deduplicated()
    {
        await enableNamespaceDeduplicationAsync();
        var topic = $"persistent://public/default/instance-names-{Guid.NewGuid():N}";
        var serviceName = "instances" + Guid.NewGuid().ToString("N")[..8];

        using var first = await startInstanceAsync(serviceName, "node-a");
        using var second = await startInstanceAsync(serviceName, "node-b");

        await using var firstSender = senderFor(first, topic);
        await using var secondSender = senderFor(second, topic);

        var a = envelope("a");
        await firstSender.SendAsync(a);
        await firstSender.SendAsync(a); // the same envelope again: dropped by the broker
        await firstSender.SendAsync(envelope("b"));

        var c = envelope("c");
        await secondSender.SendAsync(c).AsTask().WaitAsync(45.Seconds(), TestContext.Current.CancellationToken);
        await secondSender.SendAsync(c);
        await secondSender.SendAsync(envelope("d"));

        (await countMessagesAsync(first, topic, expected: 4)).ShouldBe(4);
    }

    private static async Task<IHost> startInstanceAsync(string serviceName, string? instanceId)
    {
        return await WolverineHost.ForAsync(opts =>
        {
            opts.ServiceName = serviceName;
            var pulsar = opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            if (instanceId != null)
            {
                pulsar.ProducerNamePerInstance(instanceId);
            }

            // Short enough for a test, still several of DotPulsar's 3 second retries
            opts.Transports.GetOrCreate<PulsarTransport>().ProducerNameConflictTimeout = 8.Seconds();
        });
    }

    private static PulsarSender senderFor(IHost host, string topic)
    {
        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<PulsarTransport>();
        var endpoint = transport.EndpointFor(topic);
        endpoint.DeduplicationEnabled = true;
        return new PulsarSender(runtime, endpoint, transport, CancellationToken.None);
    }

    private static Envelope envelope(string id)
    {
        return new Envelope(new DedupMessage { Id = id })
        {
            Data = Encoding.UTF8.GetBytes($"{{\"Id\":\"{id}\"}}"),
            MessageType = "dedup-message",
            ContentType = "application/json"
        };
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

    private static async Task<int> countMessagesAsync(IHost host, string topic, int expected)
    {
        var transport = host.GetRuntime().Options.Transports.GetOrCreate<PulsarTransport>();
        await using var consumer = transport.Client!.NewConsumer()
            .SubscriptionName("count-" + Guid.NewGuid().ToString("N"))
            .SubscriptionType(SubscriptionType.Exclusive)
            .InitialPosition(SubscriptionInitialPosition.Earliest)
            .Topic(topic)
            .Create();

        var count = 0;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(3.Seconds());
            IMessage<ReadOnlySequence<byte>> message;
            try
            {
                message = await consumer.Receive(cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (count >= expected)
                {
                    break;
                }

                continue;
            }

            count++;
            await consumer.Acknowledge(message, CancellationToken.None);
        }

        return count;
    }
}
