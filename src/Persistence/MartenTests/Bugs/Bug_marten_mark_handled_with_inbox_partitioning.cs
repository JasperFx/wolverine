using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

// GH-4705 made FlushOutgoingMessagesOnCommit queue the store's own mark-as-handled statement into the Marten
// session's batch. Under EnableInboxPartitioning that statement is "delete ...; update ...", and Marten's
// QueueSqlCommand refuses anything with a ';' in it:
//
//   ArgumentOutOfRangeException: You must specify one SQL command at a time because of Marten's usage of command
//   batching. ';' cannot be used as a command separator here.
//
// So every Marten-backed handler of a durable inbox or durable local queue message threw at commit time, on the one
// provider that has inbox partitioning.
public class Bug_marten_mark_handled_with_inbox_partitioning : PostgresqlContext, IAsyncLifetime
{
    private IHost? _host;
    private string _schemaName = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;

        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<IHost> startHostAsync(MessageIdentity identity, bool partitioned)
    {
        _schemaName =
            $"mark_handled_{(identity == MessageIdentity.IdOnly ? "id" : "id_dest")}_{(partitioned ? "part" : "flat")}";

        // The partitioned and unpartitioned tables differ in shape, so each combination gets a fresh schema
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(_schemaName);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PartitionedMarkHandledHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageIdentity = identity;
                opts.Durability.EnableInboxPartitioning = partitioned;

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = _schemaName;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();

        return _host;
    }

    [Theory]
    [InlineData(MessageIdentity.IdOnly, false)]
    [InlineData(MessageIdentity.IdOnly, true)]
    [InlineData(MessageIdentity.IdAndDestination, false)]
    [InlineData(MessageIdentity.IdAndDestination, true)]
    public async Task a_marten_handler_of_a_durable_message_commits_and_marks_it_handled(MessageIdentity identity,
        bool partitioned)
    {
        var host = await startHostAsync(identity, partitioned);

        var message = new PartitionedMarkHandledMessage(Guid.NewGuid());

        // Asserts on exceptions: the commit used to throw from QueueSqlCommand on every attempt
        var tracked = await host.TrackActivity().Timeout(30.Seconds())
            .ExecuteAndWaitAsync(c => c.PublishAsync(message));

        var envelope = tracked.Executed.SingleEnvelope<PartitionedMarkHandledMessage>();

        await using (var session = host.DocumentStore().QuerySession())
        {
            (await session.LoadAsync<PartitionedMarkHandledDoc>(message.Id, TestContext.Current.CancellationToken))
                .ShouldNotBeNull();
        }

        var rows = await incomingRowsForAsync(envelope.Id);
        rows.Count.ShouldBe(1);
        rows[0].Status.ShouldBe(EnvelopeStatus.Handled.ToString());
        rows[0].KeepUntil.ShouldNotBeNull();
        rows[0].KeepUntil!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    // The reason the partitioned statement has a delete in it (GH-4216): status is part of the partitioned key, so a
    // redelivered Incoming row for an identity that already has a retained Handled row cannot simply be flipped to
    // Handled. It has to go, and it has to go inside the handler's own Marten transaction.
    [Theory]
    [InlineData(MessageIdentity.IdOnly)]
    [InlineData(MessageIdentity.IdAndDestination)]
    public async Task a_redelivered_row_is_retired_inside_the_marten_session(MessageIdentity identity)
    {
        var host = await startHostAsync(identity, true);

        var runtime = host.GetRuntime();
        var inbox = runtime.Storage.Inbox;

        var original = ObjectMother.Envelope();
        original.Status = EnvelopeStatus.Incoming;

        await inbox.StoreIncomingAsync(original);
        await inbox.MarkIncomingEnvelopeAsHandledAsync(original);

        var redelivered = ObjectMother.Envelope();
        redelivered.Id = original.Id;
        redelivered.Destination = identity == MessageIdentity.IdOnly
            ? new Uri("stub://redelivered")
            : original.Destination;
        redelivered.Status = EnvelopeStatus.Incoming;

        await inbox.StoreIncomingAsync(redelivered);
        redelivered.WasPersistedInInbox = true;

        // Guard: the pair the partitioned key permits, which is the state the statement has to survive
        (await incomingRowsForAsync(original.Id)).Select(x => x.Status).ShouldBe(["Handled", "Incoming"]);

        var context = new MessageContext(runtime);
        context.ReadEnvelope(redelivered, InvocationCallback.Instance);

        await using (var session = host.Services.GetRequiredService<OutboxedSessionFactory>().OpenSession(context))
        {
            session.Store(new PartitionedMarkHandledDoc { Id = Guid.NewGuid() });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The incoming copy is gone rather than stranded, and the retained handled row is untouched
        (await incomingRowsForAsync(original.Id)).Select(x => x.Status).ShouldBe(["Handled"]);
        redelivered.Status.ShouldBe(EnvelopeStatus.Handled);
    }

    private async Task<List<(string Status, DateTimeOffset? KeepUntil)>> incomingRowsForAsync(Guid id)
    {
        var cancellation = TestContext.Current.CancellationToken;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(cancellation);

        await using var command = conn.CreateCommand();
        command.CommandText =
            $"select status, {DatabaseConstants.KeepUntil} from {_schemaName}.{DatabaseConstants.IncomingTable} where id = @id order by status";
        command.Parameters.AddWithValue("id", id);

        var rows = new List<(string, DateTimeOffset?)>();

        await using var reader = await command.ExecuteReaderAsync(cancellation);
        while (await reader.ReadAsync(cancellation))
        {
            rows.Add((reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellation)
                    ? null
                    : await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellation)));
        }

        return rows;
    }
}

public record PartitionedMarkHandledMessage(Guid Id);

public class PartitionedMarkHandledDoc
{
    public Guid Id { get; set; }
}

public static class PartitionedMarkHandledHandler
{
    public static void Handle(PartitionedMarkHandledMessage message, IDocumentSession session)
    {
        session.Store(new PartitionedMarkHandledDoc { Id = message.Id });
    }
}
