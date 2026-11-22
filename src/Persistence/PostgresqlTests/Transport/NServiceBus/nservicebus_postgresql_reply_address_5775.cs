using System.Text.Json;
using Shouldly;
using Wolverine.Postgresql.Transport.NServiceBus;

namespace PostgresqlTests.Transport.NServiceBus;

// GH-5775. NServiceBus addresses a PostgreSQL queue as "schema"."table" and splits on the first dot outside the
// quotes, so a dotted endpoint name like Sales.Endpoint is one quoted table name. The reply address used to be
// cut at the LAST dot, which turned "public"."Sales.Endpoint" into Endpoint.
public class nservicebus_postgresql_reply_address_5775
{
    private static Uri? replyUriFor(string replyToAddress)
    {
        var queue = new NServiceBusPostgresqlQueue("incoming", new NServiceBusPostgresqlTransport());
        var mapper = new NServiceBusPostgresqlEnvelopeMapper(queue, () => null);

        var headers = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [NServiceBusPostgresqlEnvelopeMapper.MessageIdHeader] = Guid.NewGuid().ToString(),
            [NServiceBusPostgresqlEnvelopeMapper.ReplyToAddressHeader] = replyToAddress
        });

        return mapper.MapIncoming(Guid.NewGuid(), headers, []).ReplyUri;
    }

    [Theory]
    [InlineData("\"public\".\"Sales.Endpoint\"", "Sales.Endpoint")]
    [InlineData("\"public\".\"nsb\"", "nsb")]
    [InlineData("\"nsb\"", "nsb")]
    [InlineData("nsb", "nsb")]
    [InlineData("public.nsb", "nsb")]
    [InlineData("\"my.schema\".\"Sales.Endpoint\"", "Sales.Endpoint")]
    [InlineData("\"public\".\"has\"\"quote\"", "has\"quote")]
    public void reply_address_maps_to_the_table_name_nservicebus_would_parse(string address, string expectedQueue)
    {
        replyUriFor(address).ShouldBe(NServiceBusPostgresqlQueue.ToUri(expectedQueue));
    }
}
