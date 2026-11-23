using System.Text;
using System.Text.Json;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport.NServiceBus;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace PostgresqlTests.Transport.NServiceBus;

// GH-5814. NServiceBus names a PostgreSQL queue table after its endpoint and quotes it ("public"."Sales.Endpoint"),
// so the name is case-sensitive. The transport looked queues up by Uri.Host, which System.Uri lowercases, so a reply
// to Sales.Endpoint went to a sales.endpoint table that does not exist, or to the wrong one.
public class nservicebus_postgresql_reply_queue_case_5814
{
    private readonly NServiceBusPostgresqlTransport _transport = new();

    private NServiceBusPostgresqlQueue lookup(Uri uri) =>
        (NServiceBusPostgresqlQueue)_transport.GetOrCreateEndpoint(uri);

    [Fact]
    public void a_mixed_case_queue_name_survives_the_lookup()
    {
        lookup(NServiceBusPostgresqlQueue.ToUri("Sales.Endpoint")).Name.ShouldBe("Sales.Endpoint");
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

        lookup(NServiceBusPostgresqlQueue.ToUri("nsb_orders")).ShouldBeSameAs(queue);
        lookup(NServiceBusPostgresqlQueue.ToUri("nsb_other")).Name.ShouldBe("nsb_other");
    }

    [Fact]
    public void a_uri_rebuilt_from_its_string_form_finds_the_one_configured_queue_it_means()
    {
        var queue = _transport.Queues["Sales.Endpoint"];

        // Uri.ToString() lowercases the host, so the case is gone from this one
        var rebuilt = new Uri(queue.Uri.ToString());

        lookup(rebuilt).ShouldBeSameAs(queue);
    }

    [Fact]
    public void two_queues_that_differ_only_in_case_stay_apart()
    {
        var upper = _transport.Queues["Sales"];
        var lower = _transport.Queues["sales"];

        lookup(NServiceBusPostgresqlQueue.ToUri("Sales")).ShouldBeSameAs(upper);
        lookup(NServiceBusPostgresqlQueue.ToUri("sales")).ShouldBeSameAs(lower);
    }

    [Fact]
    public void a_name_that_is_not_a_valid_host_survives_the_lookup()
    {
        lookup(NServiceBusPostgresqlQueue.ToUri("Sales Endpoint")).Name.ShouldBe("Sales Endpoint");
    }

    [Fact]
    public void the_reply_uri_of_an_nservicebus_message_resolves_to_its_mixed_case_queue()
    {
        var mapper = new NServiceBusPostgresqlEnvelopeMapper(_transport.Queues["incoming"], () => null);
        var headers = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [NServiceBusPostgresqlEnvelopeMapper.MessageIdHeader] = Guid.NewGuid().ToString(),
            [NServiceBusPostgresqlEnvelopeMapper.ReplyToAddressHeader] = "\"public\".\"Sales.Endpoint\""
        });

        var envelope = mapper.MapIncoming(Guid.NewGuid(), headers, []);

        lookup(envelope.ReplyUri!).Name.ShouldBe("Sales.Endpoint");
    }
}

// GH-5814 against PostgreSQL: a message whose NServiceBus reply address is a mixed-case endpoint that Wolverine was
// never told about gets its reply in that endpoint's own table.
public class nservicebus_postgresql_reply_queue_case_5814_integration : IAsyncLifetime
{
    private readonly string _incomingName = "nsb_reply_in_" + Guid.NewGuid().ToString("N");
    private readonly string _replyName = "Reply5814.Endpoint" + Guid.NewGuid().ToString("N")[..8];
    private IHost _host = null!;
    private NServiceBusPostgresqlTransport _transport = null!;
    private NServiceBusPostgresqlQueue _replyQueue = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nsbreply5814");
                opts.UseNServiceBusPostgresqlInterop(autoProvision: true);

                opts.ListenToNServiceBusPostgresqlQueue(_incomingName);

                opts.Discovery.DisableConventionalDiscovery().IncludeType<ReplyCaseRequest5814Handler>();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _transport = _host.Services.GetRequiredService<IWolverineRuntime>().Options.Transports
            .GetOrCreate<NServiceBusPostgresqlTransport>();

        // The NServiceBus endpoint's own queue table, created the way NServiceBus names it. Deliberately not
        // registered with the transport: Wolverine only learns about this endpoint from the reply address.
        _replyQueue = new NServiceBusPostgresqlQueue(_replyName, _transport);
        await _replyQueue.SetupAsync(NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        // Through the host's data source, so before the host goes
        await _replyQueue.TeardownAsync(NullLogger.Instance);

        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_reply_lands_in_the_mixed_case_reply_queue()
    {
        var id = Guid.NewGuid();
        var messageType = typeof(ReplyCaseRequest5814);
        var headers = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [NServiceBusPostgresqlEnvelopeMapper.MessageIdHeader] = Guid.NewGuid().ToString(),
            [NServiceBusPostgresqlEnvelopeMapper.ContentTypeHeader] = "application/json",
            [NServiceBusPostgresqlEnvelopeMapper.EnclosedMessageTypesHeader] =
                $"{messageType.FullName}, {messageType.Assembly.GetName().Name}",
            [NServiceBusPostgresqlEnvelopeMapper.ReplyToAddressHeader] = $"\"public\".\"{_replyName}\""
        });

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new NpgsqlCommand(
                $"insert into {_transport.Queues[_incomingName].TableIdentifier} (id, headers, body) values (:id, :headers, :body)",
                conn);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("headers", headers);
            cmd.Parameters.AddWithValue("body", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ReplyCaseRequest5814(id))));
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (await _replyQueue.CountAsync() == 0)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, $"no reply reached the {_replyName} queue table");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        (await _replyQueue.CountAsync()).ShouldBe(1);
    }
}

public record ReplyCaseRequest5814(Guid Id);

public record ReplyCaseResponse5814(Guid Id);

public class ReplyCaseRequest5814Handler
{
    public static ValueTask Handle(ReplyCaseRequest5814 message, IMessageContext context) =>
        context.RespondToSenderAsync(new ReplyCaseResponse5814(message.Id));
}
