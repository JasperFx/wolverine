using System.Text.Json;
using Shouldly;
using Wolverine.SqlServer.Transport.NServiceBus;

namespace SqlServerTests.Transport.NServiceBus;

// GH-5814. The SQL Server interop transport looked queues up by Uri.Host, which System.Uri lowercases. Under SQL
// Server's default case-insensitive collation Sales.Endpoint and sales.endpoint are the same table, but on a database
// with a case-sensitive collation a reply to Sales.Endpoint went to a sales.endpoint table that does not exist. And a
// configured mixed-case queue was not found by its own Uri: the lookup minted a second, lowercase queue.
public class nservicebus_sql_server_reply_queue_case_5814
{
    private readonly NServiceBusSqlServerTransport _transport = new();

    private NServiceBusSqlServerQueue lookup(Uri uri) =>
        (NServiceBusSqlServerQueue)_transport.GetOrCreateEndpoint(uri);

    [Fact]
    public void a_mixed_case_queue_name_survives_the_lookup()
    {
        lookup(NServiceBusSqlServerQueue.ToUri("Sales.Endpoint")).Name.ShouldBe("Sales.Endpoint");
    }

    [Fact]
    public void a_configured_mixed_case_queue_is_found_by_its_own_uri()
    {
        var queue = _transport.Queues["Sales.Endpoint"];

        lookup(queue.Uri).ShouldBeSameAs(queue);
        _transport.Queues.Count().ShouldBe(1);
    }

    [Fact]
    public void a_lowercase_queue_name_is_unchanged()
    {
        var queue = _transport.Queues["nsb_orders"];

        lookup(NServiceBusSqlServerQueue.ToUri("nsb_orders")).ShouldBeSameAs(queue);
        lookup(NServiceBusSqlServerQueue.ToUri("nsb_other")).Name.ShouldBe("nsb_other");
    }

    [Fact]
    public void a_uri_rebuilt_from_its_string_form_finds_the_one_configured_queue_it_means()
    {
        var queue = _transport.Queues["Sales.Endpoint"];

        // Uri.ToString() lowercases the host, so the case is gone from this one
        lookup(new Uri(queue.Uri.ToString())).ShouldBeSameAs(queue);
    }

    [Fact]
    public void two_queues_that_differ_only_in_case_stay_apart()
    {
        var upper = _transport.Queues["Sales"];
        var lower = _transport.Queues["sales"];

        lookup(NServiceBusSqlServerQueue.ToUri("Sales")).ShouldBeSameAs(upper);
        lookup(NServiceBusSqlServerQueue.ToUri("sales")).ShouldBeSameAs(lower);
    }

    [Fact]
    public void the_reply_uri_of_an_nservicebus_message_resolves_to_its_mixed_case_queue()
    {
        var mapper = new NServiceBusSqlServerEnvelopeMapper(_transport.Queues["incoming"], () => null);
        var headers = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [NServiceBusSqlServerEnvelopeMapper.MessageIdHeader] = Guid.NewGuid().ToString(),
            [NServiceBusSqlServerEnvelopeMapper.ReplyToAddressHeader] = "Sales.Endpoint@[dbo]@[nservicebus]"
        });

        var envelope = mapper.MapIncoming(Guid.NewGuid(), headers, []);

        lookup(envelope.ReplyUri!).Name.ShouldBe("Sales.Endpoint");
    }
}
