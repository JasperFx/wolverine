using System.Text;
using Dekaf.Consumer;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// The id a durable Kafka listener gives a record that has none of its own. No broker required; the end to
/// end behavior is in <see cref="redelivered_record_without_id_header_is_handled_once" />.
/// </summary>
public class kafka_record_identity
{
    private static readonly KafkaRecordIdentity theIdentity = new("kafka", null, "orders-group");

    [Fact]
    public void name_based_ids_follow_rfc_9562()
    {
        // RFC 9562, appendix A.4: version 5 of "www.example.com" in the DNS namespace
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        KafkaRecordIdentity.CreateVersion5(dns, "www.example.com"u8)
            .ShouldBe(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"));
    }

    [Fact]
    public void a_record_gets_a_version_5_uuid()
    {
        var id = theIdentity.IdFor("orders", 0, 42);

        id.Version.ShouldBe(5);
        (id.Variant & 0b1100).ShouldBe(0b1000);
    }

    [Fact]
    public void the_same_record_always_gets_the_same_id()
    {
        // A different listener instance, as after a restart, a rebalance onto another node or another process
        var afterRestart = new KafkaRecordIdentity("kafka", null, "orders-group");

        afterRestart.IdFor("orders", 3, 1234).ShouldBe(theIdentity.IdFor("orders", 3, 1234));
    }

    [Fact]
    public void every_coordinate_is_part_of_the_id()
    {
        var ids = new[]
        {
            theIdentity.IdFor("orders", 0, 7),
            theIdentity.IdFor("orders", 0, 8),
            theIdentity.IdFor("orders", 1, 7),
            theIdentity.IdFor("invoices", 0, 7),
            new KafkaRecordIdentity("kafka", null, "billing-group").IdFor("orders", 0, 7),
            new KafkaRecordIdentity("kafka", "tenant-a", "orders-group").IdFor("orders", 0, 7),
            new KafkaRecordIdentity("kafka", "tenant-b", "orders-group").IdFor("orders", 0, 7),
            new KafkaRecordIdentity("americas", null, "orders-group").IdFor("orders", 0, 7)
        };

        ids.Distinct().Count().ShouldBe(ids.Length);
    }

    [Fact]
    public void components_do_not_run_into_each_other()
    {
        new KafkaRecordIdentity("kafka", null, "ab").IdFor("c", 0, 0)
            .ShouldNotBe(new KafkaRecordIdentity("kafka", null, "a").IdFor("bc", 0, 0));

        new KafkaRecordIdentity("kafka", "t", "g").IdFor("orders", 0, 0)
            .ShouldNotBe(new KafkaRecordIdentity("kafka", null, "tg").IdFor("orders", 0, 0));
    }

    [Fact]
    public void a_name_too_long_for_the_stack_is_still_stable()
    {
        var group = new string('g', 2000);

        new KafkaRecordIdentity("kafka", null, group).IdFor("orders", 0, 7)
            .ShouldBe(new KafkaRecordIdentity("kafka", null, group).IdFor("orders", 0, 7));

        new KafkaRecordIdentity("kafka", null, group).IdFor("orders", 0, 7)
            .ShouldNotBe(new KafkaRecordIdentity("kafka", null, group + "g").IdFor("orders", 0, 7));
    }

    [Fact]
    public void a_record_without_an_id_header_gets_the_id_of_its_position()
    {
        var mapper = defaultMapper();

        var first = mapper.CreateEnvelope(record(offset: 7), theIdentity);
        var redelivered = mapper.CreateEnvelope(record(offset: 7), theIdentity);

        first.Id.ShouldBe(theIdentity.IdFor("orders", 2, 7));
        redelivered.Id.ShouldBe(first.Id);
    }

    [Fact]
    public void the_id_header_wins()
    {
        var id = Guid.NewGuid();

        var envelope = defaultMapper().CreateEnvelope(record(offset: 7, idHeader: id.ToString()), theIdentity);

        envelope.Id.ShouldBe(id);
    }

    [Fact]
    public void an_unreadable_id_header_gets_the_id_of_the_position()
    {
        // KafkaEnvelopeMapper reads it as Guid.Empty, which is no id at all
        var envelope = defaultMapper().CreateEnvelope(record(offset: 7, idHeader: "order-1"), theIdentity);

        envelope.Id.ShouldBe(theIdentity.IdFor("orders", 2, 7));
    }

    [Fact]
    public void the_raw_json_mapper_gets_the_id_of_the_position()
    {
        var mapper = new JsonOnlyMapper(new KafkaTopic(new KafkaTransport(), "orders", EndpointRole.Application));

        mapper.CreateEnvelope(record(offset: 7), theIdentity).Id.ShouldBe(theIdentity.IdFor("orders", 2, 7));
    }

    [Fact]
    public void an_id_a_custom_mapper_assigns_wins()
    {
        var id = Guid.NewGuid();
        var mapper = Substitute.For<IKafkaEnvelopeMapper>();
        mapper.When(x => x.MapIncomingToEnvelope(Arg.Any<Envelope>(), Arg.Any<ConsumeResult<string, byte[]>>()))
            .Do(ci => ci.Arg<Envelope>().Id = id);

        mapper.CreateEnvelope(record(offset: 7), theIdentity).Id.ShouldBe(id);
    }

    [Fact]
    public void a_custom_mapper_that_assigns_no_id_gets_the_id_of_the_position()
    {
        var mapper = Substitute.For<IKafkaEnvelopeMapper>();

        mapper.CreateEnvelope(record(offset: 7), theIdentity).Id.ShouldBe(theIdentity.IdFor("orders", 2, 7));
    }

    [Fact]
    public void without_an_identity_the_envelope_is_left_as_the_mapper_built_it()
    {
        defaultMapper().CreateEnvelope(record(offset: 7), null).Id.ShouldBe(Guid.Empty);

        var mapper = new JsonOnlyMapper(new KafkaTopic(new KafkaTransport(), "orders", EndpointRole.Application));
        var envelope = mapper.CreateEnvelope(record(offset: 7), null);
        envelope.Id.ShouldNotBe(Guid.Empty);
        envelope.Id.ShouldNotBe(theIdentity.IdFor("orders", 2, 7));
    }

    [Theory]
    [InlineData(EndpointMode.Durable, true)]
    [InlineData(EndpointMode.BufferedInMemory, false)]
    [InlineData(EndpointMode.Inline, false)]
    public async Task only_a_durable_listener_derives_the_id(EndpointMode mode, bool derived)
    {
        var transport = new KafkaTransport();
        var topic = transport.Topics["orders"];
        topic.Mode = mode;

        var envelope = await receiveOneAsync(topic, transport, tenantTransport: null);

        (envelope.Id == new KafkaRecordIdentity("kafka", null, "orders-group").IdFor("orders", 2, 7))
            .ShouldBe(derived);
    }

    [Fact]
    public async Task a_tenant_listener_keeps_its_records_apart_from_the_default_cluster()
    {
        var transport = new KafkaTransport();
        transport.Tenants["tenant-a"] = new KafkaTenant("tenant-a") { BootstrapServers = "tenant-a:9092" };
        var topic = transport.Topics["orders"];
        topic.Mode = EndpointMode.Durable;

        var envelope = await receiveOneAsync(topic, transport, transport.Tenants["tenant-a"].Transport);

        envelope.Id.ShouldBe(new KafkaRecordIdentity("kafka", "tenant-a", "orders-group").IdFor("orders", 2, 7));
        envelope.Id.ShouldNotBe(new KafkaRecordIdentity("kafka", null, "orders-group").IdFor("orders", 2, 7));
    }

    [Fact]
    public async Task a_durable_topic_group_listener_derives_the_id()
    {
        var transport = new KafkaTransport();
        var group = new KafkaTopicGroup(transport, ["orders", "invoices"], EndpointRole.Application)
        {
            Mode = EndpointMode.Durable
        };
        group.EnvelopeMapper = new KafkaEnvelopeMapper(group);

        var received = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new KafkaListener(group, new KafkaConsumerOptions { GroupId = "orders-group" },
            consumerOf(record(offset: 7)), group.TopicNames, receiverInto(received), NullLogger<KafkaListener>.Instance,
            TimeSpan.FromSeconds(5));

        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        envelope.Id.ShouldBe(new KafkaRecordIdentity("kafka", null, "orders-group").IdFor("orders", 2, 7));
    }

    private static async Task<Envelope> receiveOneAsync(KafkaTopic topic, KafkaTransport transport,
        KafkaTransport? tenantTransport)
    {
        topic.EnvelopeMapper = new KafkaEnvelopeMapper(topic);

        var received = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new KafkaListener(topic, new KafkaConsumerOptions { GroupId = "orders-group" },
            consumerOf(record(offset: 7)), [topic.TopicName], receiverInto(received), NullLogger<KafkaListener>.Instance,
            TimeSpan.FromSeconds(5), tenantTransport);

        transport.Protocol.ShouldBe("kafka");

        return await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static IReceiver receiverInto(TaskCompletionSource<Envelope> received)
    {
        var receiver = Substitute.For<IReceiver>();
        receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>()).Returns(ci =>
        {
            received.TrySetResult(ci.Arg<Envelope>());
            return ValueTask.CompletedTask;
        });

        return receiver;
    }

    // Hands out the one record, then waits the way the real ConsumeOneAsync does until the listener stops. A
    // zero-timeout poll only drains records already fetched, so it finds nothing.
    private static IKafkaConsumer<string, byte[]> consumerOf(ConsumeResult<string, byte[]> result)
    {
        var consumer = Substitute.For<IKafkaConsumer<string, byte[]>>();
        var handedOut = 0;

        consumer.ConsumeOneAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => next(ci.ArgAt<TimeSpan>(0), ci.ArgAt<CancellationToken>(1)));

        async ValueTask<ConsumeResult<string, byte[]>?> next(TimeSpan timeout, CancellationToken token)
        {
            if (timeout == TimeSpan.Zero)
            {
                return null;
            }

            if (Interlocked.Exchange(ref handedOut, 1) == 0)
            {
                return result;
            }

            await Task.Delay(Timeout.Infinite, token);
            return null;
        }

        return consumer;
    }

    private static KafkaEnvelopeMapper defaultMapper()
    {
        return new KafkaEnvelopeMapper(new KafkaTopic(new KafkaTransport(), "orders", EndpointRole.Application));
    }

    private static ConsumeResult<string, byte[]> record(long offset, string? idHeader = null)
    {
        var headers = new List<KeyValuePair<string, byte[]?>>
        {
            new("ce_id", Encoding.UTF8.GetBytes("order-1")),
            new("ce_type", Encoding.UTF8.GetBytes("com.example.order.placed.v1"))
        };

        if (idHeader != null)
        {
            headers.Add(new(EnvelopeConstants.IdKey, Encoding.UTF8.GetBytes(idHeader)));
        }

        return TestRecords.Incoming("orders", "order-1", "{}"u8.ToArray(), headers, partition: 2, offset: offset);
    }
}
