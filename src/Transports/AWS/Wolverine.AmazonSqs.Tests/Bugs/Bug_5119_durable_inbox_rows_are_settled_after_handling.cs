using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Npgsql;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;

namespace Wolverine.AmazonSqs.Tests.Bugs;

public record Bug5119Probe(int Sequence);

public class Bug5119ProbeHandler
{
    public static readonly ConcurrentBag<Guid> Handled = [];

    public void Handle(Bug5119Probe message, Envelope envelope) => Handled.Add(envelope.Id);
}

/// <summary>
/// GH-5119. A durable SQS listener settles the broker delivery BEFORE the handler runs (GH-4026), and
/// SQS records that settle on <c>Envelope.HasBeenAcked</c> (GH-4489). <c>MessageContext.CompleteAsync</c>
/// short-circuits on that same flag, so after a successful handler the durable receiver's
/// <c>CompleteAsync</c> was never called and the inbox row stayed 'Incoming', owned by the live node.
/// </summary>
public class Bug_5119_durable_inbox_rows_are_settled_after_handling
{
    private const string Schema = "bug_5119";
    private const string Queue = "bug-5119";
    private const int MessageCount = 5;

    [Fact]
    public async Task handled_envelopes_are_not_left_incoming_in_the_inbox()
    {
        Bug5119ProbeHandler.Handled.Clear();

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

            opts.ListenToSqsQueue(Queue).UseDurableInbox();
            opts.PublishMessage<Bug5119Probe>().ToSqsQueue(Queue);

            opts.DisableConventionalDiscovery().IncludeType<Bug5119ProbeHandler>();
        });

        await host.ResetResourceState(TestContext.Current.CancellationToken);

        var bus = host.MessageBus();
        for (var i = 0; i < MessageCount; i++)
        {
            await bus.PublishAsync(new Bug5119Probe(i));
        }

        for (var i = 0; i < 120 && Bug5119ProbeHandler.Handled.Count < MessageCount; i++)
        {
            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        }

        // Without every message handled, the rest of this proves nothing
        Bug5119ProbeHandler.Handled.Count.ShouldBe(MessageCount);

        var handled = Bug5119ProbeHandler.Handled.ToHashSet();

        // Poll rather than sleep: settlement goes through the completion coalescer
        var unsettled = await unsettledRowsAsync(handled);
        for (var i = 0; i < 40 && unsettled.Any(); i++)
        {
            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
            unsettled = await unsettledRowsAsync(handled);
        }

        unsettled.ShouldBeEmpty();
    }

    private static async Task<List<string>> unsettledRowsAsync(HashSet<Guid> handled)
    {
        var unsettled = new List<string>();

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select id, status, owner_id, attempts from {Schema}.wolverine_incoming_envelopes where status <> 'Handled'";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0);
            if (handled.Contains(id))
            {
                unsettled.Add($"{id} status={reader.GetString(1)} owner_id={reader.GetInt32(2)} attempts={reader.GetInt32(3)}");
            }
        }

        return unsettled;
    }
}
