using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// The id a durable Redis stream listener gives an entry that has none of its own. No broker required; the end to
/// end behavior is in <see cref="redelivered_entry_without_wolverine_id_is_handled_once" />.
/// </summary>
public class redis_stream_entry_identity
{
    private static readonly RedisStreamEntryIdentity theIdentity = new("redis", null, 0, "orders-group", "orders");

    [Fact]
    public void name_based_ids_follow_rfc_9562()
    {
        // RFC 9562, appendix A.4: version 5 of "www.example.com" in the DNS namespace
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        RedisStreamEntryIdentity.CreateVersion5(dns, "www.example.com"u8)
            .ShouldBe(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"));
    }

    [Fact]
    public void an_entry_gets_a_version_5_uuid()
    {
        var id = theIdentity.IdFor("1700000000000-0");

        id.Version.ShouldBe(5);
        (id.Variant & 0b1100).ShouldBe(0b1000);
    }

    [Fact]
    public void the_same_entry_always_gets_the_same_id()
    {
        // A different listener instance, as after a restart or on the node that reclaims the entry with XAUTOCLAIM
        var afterRestart = new RedisStreamEntryIdentity("redis", null, 0, "orders-group", "orders");

        afterRestart.IdFor("1700000000000-0").ShouldBe(theIdentity.IdFor("1700000000000-0"));
    }

    [Fact]
    public void every_coordinate_is_part_of_the_id()
    {
        var ids = new[]
        {
            theIdentity.IdFor("1700000000000-0"),
            theIdentity.IdFor("1700000000000-1"),
            theIdentity.IdFor("1700000000001-0"),
            new RedisStreamEntryIdentity("redis", null, 0, "orders-group", "invoices").IdFor("1700000000000-0"),
            new RedisStreamEntryIdentity("redis", null, 0, "billing-group", "orders").IdFor("1700000000000-0"),
            new RedisStreamEntryIdentity("redis", null, 3, "orders-group", "orders").IdFor("1700000000000-0"),
            new RedisStreamEntryIdentity("redis", "tenant-a", 0, "orders-group", "orders").IdFor("1700000000000-0"),
            new RedisStreamEntryIdentity("redis", "tenant-b", 0, "orders-group", "orders").IdFor("1700000000000-0"),
            new RedisStreamEntryIdentity("americas", null, 0, "orders-group", "orders").IdFor("1700000000000-0")
        };

        ids.Distinct().Count().ShouldBe(ids.Length);
    }

    [Fact]
    public void components_do_not_run_into_each_other()
    {
        new RedisStreamEntryIdentity("redis", null, 0, "ab", "c").IdFor("1-0")
            .ShouldNotBe(new RedisStreamEntryIdentity("redis", null, 0, "a", "bc").IdFor("1-0"));

        new RedisStreamEntryIdentity("redis", "t", 0, "g", "orders").IdFor("1-0")
            .ShouldNotBe(new RedisStreamEntryIdentity("redis", null, 0, "tg", "orders").IdFor("1-0"));

        new RedisStreamEntryIdentity("redis", null, 0, "orders-group", "orders-1").IdFor("1-0")
            .ShouldNotBe(new RedisStreamEntryIdentity("redis", null, 0, "orders-group", "orders").IdFor("-11-0"));
    }

    [Fact]
    public void a_name_too_long_for_the_stack_is_still_stable()
    {
        var stream = new string('s', 2000);

        new RedisStreamEntryIdentity("redis", null, 0, "orders-group", stream).IdFor("1-0")
            .ShouldBe(new RedisStreamEntryIdentity("redis", null, 0, "orders-group", stream).IdFor("1-0"));

        new RedisStreamEntryIdentity("redis", null, 0, "orders-group", stream).IdFor("1-0")
            .ShouldNotBe(new RedisStreamEntryIdentity("redis", null, 0, "orders-group", stream + "s").IdFor("1-0"));
    }

    [Fact]
    public void an_entry_without_a_wolverine_id_gets_the_id_of_its_position()
    {
        var mapper = defaultMapper();

        var first = mapper.CreateEnvelope("orders", entry("1700000000000-0"), theIdentity);
        var redelivered = mapper.CreateEnvelope("orders", entry("1700000000000-0"), theIdentity);

        first.Id.ShouldBe(theIdentity.IdFor("1700000000000-0"));
        redelivered.Id.ShouldBe(first.Id);
    }

    [Fact]
    public void the_wolverine_id_field_wins()
    {
        var id = Guid.NewGuid();

        var envelope = defaultMapper().CreateEnvelope("orders", entry("1700000000000-0", idField: id.ToString()),
            theIdentity);

        envelope.Id.ShouldBe(id);
    }

    [Fact]
    public void an_unreadable_wolverine_id_field_gets_the_id_of_the_position()
    {
        // RedisEnvelopeMapper reads it as Guid.Empty, which is no id at all
        var envelope = defaultMapper().CreateEnvelope("orders", entry("1700000000000-0", idField: "order-1"),
            theIdentity);

        envelope.Id.ShouldBe(theIdentity.IdFor("1700000000000-0"));
    }

    [Fact]
    public void an_id_a_custom_mapper_assigns_wins()
    {
        var id = Guid.NewGuid();
        var mapper = Substitute.For<IRedisEnvelopeMapper>();
        mapper.When(x => x.MapIncomingToEnvelope(Arg.Any<Envelope>(), Arg.Any<StreamEntry>()))
            .Do(ci => ci.Arg<Envelope>().Id = id);

        mapper.CreateEnvelope("orders", entry("1700000000000-0"), theIdentity).Id.ShouldBe(id);
    }

    [Fact]
    public void a_custom_mapper_that_assigns_no_id_gets_the_id_of_the_position()
    {
        var mapper = Substitute.For<IRedisEnvelopeMapper>();

        mapper.CreateEnvelope("orders", entry("1700000000000-0"), theIdentity).Id
            .ShouldBe(theIdentity.IdFor("1700000000000-0"));
    }

    [Fact]
    public void without_an_identity_the_envelope_is_left_as_the_mapper_built_it()
    {
        var envelope = defaultMapper().CreateEnvelope("orders", entry("1700000000000-0"), null);

        envelope.Id.ShouldBe(Guid.Empty);
        envelope.TopicName.ShouldBe("orders");
        envelope.Headers[RedisEnvelopeMapper.RedisEntryIdHeader].ShouldBe("1700000000000-0");
    }

    [Theory]
    [InlineData(EndpointMode.Durable, true)]
    [InlineData(EndpointMode.BufferedInMemory, false)]
    [InlineData(EndpointMode.Inline, false)]
    public void only_a_durable_listener_derives_the_id(EndpointMode mode, bool derived)
    {
        var transport = new RedisTransport();
        var endpoint = ordersEndpoint(transport, databaseId: 0);
        endpoint.Mode = mode;

        var listener = listenerFor(transport, endpoint, connection: null);

        (listener.EntryIdentity != null).ShouldBe(derived);
        listener.EntryIdentity?.IdFor("1700000000000-0").ShouldBe(theIdentity.IdFor("1700000000000-0"));
    }

    [Fact]
    public void the_database_of_the_stream_is_part_of_the_listeners_identity()
    {
        var transport = new RedisTransport();
        var endpoint = ordersEndpoint(transport, databaseId: 3);
        endpoint.Mode = EndpointMode.Durable;

        listenerFor(transport, endpoint, connection: null).EntryIdentity!.IdFor("1700000000000-0")
            .ShouldBe(new RedisStreamEntryIdentity("redis", null, 3, "orders-group", "orders").IdFor("1700000000000-0"));
    }

    [Fact]
    public void a_tenant_listener_keeps_its_entries_apart_from_the_default_server()
    {
        var transport = new RedisTransport();
        var tenantConnection = Substitute.For<IConnectionMultiplexer>();
        transport.Tenants["tenant-a"] = new RedisTenant("tenant-a") { ExternalConnection = tenantConnection };
        transport.GetTenantConnection(transport.Tenants["tenant-a"]).ShouldBeSameAs(tenantConnection);

        var endpoint = ordersEndpoint(transport, databaseId: 0);
        endpoint.Mode = EndpointMode.Durable;

        var id = listenerFor(transport, endpoint, tenantConnection).EntryIdentity!.IdFor("1700000000000-0");

        id.ShouldBe(new RedisStreamEntryIdentity("redis", "tenant-a", 0, "orders-group", "orders")
            .IdFor("1700000000000-0"));
        id.ShouldNotBe(theIdentity.IdFor("1700000000000-0"));
    }

    private static RedisStreamEndpoint ordersEndpoint(RedisTransport transport, int databaseId)
    {
        var endpoint = transport.StreamEndpoint("orders", databaseId);
        endpoint.ConsumerGroup = "orders-group";
        endpoint.ConsumerName = "consumer-1";
        endpoint.IsListener = true;
        return endpoint;
    }

    private static RedisStreamListener listenerFor(RedisTransport transport, RedisStreamEndpoint endpoint,
        IConnectionMultiplexer? connection)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());

        return new RedisStreamListener(transport, endpoint, runtime, Substitute.For<IReceiver>(), connection);
    }

    private static RedisEnvelopeMapper defaultMapper()
    {
        return new RedisEnvelopeMapper(new RedisTransport().StreamEndpoint("orders"));
    }

    // An entry from a producer that is not Wolverine: its payload in the "payload" field the default mapper reads,
    // and its own fields beside it, but no "wolverine-id"
    private static StreamEntry entry(string entryId, string? idField = null)
    {
        var values = new List<NameValueEntry>
        {
            new("payload", "{\"OrderId\":\"order-1\"}"u8.ToArray()),
            new("source", "/orders")
        };

        if (idField != null)
        {
            values.Add(new NameValueEntry("wolverine-" + EnvelopeConstants.IdKey, idField));
        }

        return new StreamEntry(entryId, values.ToArray());
    }
}
