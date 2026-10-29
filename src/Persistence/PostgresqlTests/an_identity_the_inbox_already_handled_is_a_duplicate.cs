using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PostgresqlTests;

/// <summary>
/// The durable inbox refuses a delivery whose identity it already holds, and it keeps a handled row for
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> so that a broker redelivery arriving after the first
/// copy was handled is refused as well. Without partitioning the refusal is the primary key. With
/// <see cref="DurabilitySettings.EnableInboxPartitioning" /> the key gains the status column, a Handled row no longer
/// collides with an Incoming insert of the same identity, and the redelivery was stored and handled a second time.
///
/// <para>
/// Every case runs with and without partitioning, because the unpartitioned store is the behavior the partitioned
/// one has to match. Both <see cref="MessageIdentity" /> styles run, because the refusal has to look for the identity
/// the table is keyed with: under IdOnly a copy at another destination is the same message.
/// </para>
/// </summary>
public class an_identity_the_inbox_already_handled_is_a_duplicate
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(MessageIdentity.IdOnly, false, EnvelopeStatus.Incoming)]
    [InlineData(MessageIdentity.IdOnly, true, EnvelopeStatus.Incoming)]
    [InlineData(MessageIdentity.IdAndDestination, false, EnvelopeStatus.Incoming)]
    [InlineData(MessageIdentity.IdAndDestination, true, EnvelopeStatus.Incoming)]
    [InlineData(MessageIdentity.IdOnly, false, EnvelopeStatus.Scheduled)]
    [InlineData(MessageIdentity.IdOnly, true, EnvelopeStatus.Scheduled)]
    [InlineData(MessageIdentity.IdAndDestination, false, EnvelopeStatus.Scheduled)]
    [InlineData(MessageIdentity.IdAndDestination, true, EnvelopeStatus.Scheduled)]
    public async Task storing_it_again_is_refused(MessageIdentity identity, bool partitioned, EnvelopeStatus status)
    {
        await using var inbox = await HandledInbox.StartAsync(identity, partitioned);

        var handled = await inbox.HandleAsync();

        // A delayed message is received as Scheduled, and a retry booked after the fact is inserted as Scheduled:
        // either way the identity already completed
        var redelivered = inbox.CopyOf(handled, handled.Destination!, status);

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(() =>
            inbox.Store.Inbox.StoreIncomingAsync(redelivered));

        (await inbox.StatusesOfAsync(handled.Id)).ShouldBe(["Handled"]);
    }

    [Theory]
    [InlineData(MessageIdentity.IdOnly, false)]
    [InlineData(MessageIdentity.IdOnly, true)]
    [InlineData(MessageIdentity.IdAndDestination, false)]
    [InlineData(MessageIdentity.IdAndDestination, true)]
    public async Task storing_it_again_in_a_batch_refuses_it_and_stores_nothing_else(MessageIdentity identity,
        bool partitioned)
    {
        await using var inbox = await HandledInbox.StartAsync(identity, partitioned);

        var handled = await inbox.HandleAsync();
        var fresh = inbox.NewEnvelope(handled.Destination!);
        var redelivered = inbox.CopyOf(handled, handled.Destination!, EnvelopeStatus.Incoming);

        var ex = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(() =>
            inbox.Store.Inbox.StoreIncomingAsync([fresh, redelivered]));

        // Only the redelivery is a duplicate: the receiver settles what is reported here without handling it
        ex.Duplicates.Select(x => x.Id).ShouldBe([handled.Id]);

        // All or nothing: the receiver re-runs every envelope of a refused batch one at a time, and a fresh
        // envelope committed here would read as a duplicate of itself on that second store
        (await inbox.Store.Inbox.ExistsAsync(fresh, Ct)).ShouldBeFalse();
        (await inbox.StatusesOfAsync(handled.Id)).ShouldBe(["Handled"]);
    }

    [Theory]
    [InlineData(MessageIdentity.IdOnly, false)]
    [InlineData(MessageIdentity.IdOnly, true)]
    [InlineData(MessageIdentity.IdAndDestination, false)]
    [InlineData(MessageIdentity.IdAndDestination, true)]
    public async Task storing_it_again_inside_the_callers_transaction_is_refused(MessageIdentity identity,
        bool partitioned)
    {
        await using var inbox = await HandledInbox.StartAsync(identity, partitioned);

        var handled = await inbox.HandleAsync();
        var redelivered = inbox.CopyOf(handled, handled.Destination!, EnvelopeStatus.Incoming);

        // The path a database-backed external table takes into the inbox
        var database = (IMessageDatabase)inbox.Store;
        await using (var conn = await database.DataSource.OpenConnectionAsync(Ct))
        {
            await using var tx = await conn.BeginTransactionAsync(Ct);

            await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(() =>
                database.StoreIncomingAsync(tx, [redelivered]));

            await tx.RollbackAsync(Ct);
        }

        (await inbox.StatusesOfAsync(handled.Id)).ShouldBe(["Handled"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task under_id_only_the_same_id_handled_at_another_destination_is_a_duplicate(bool partitioned)
    {
        await using var inbox = await HandledInbox.StartAsync(MessageIdentity.IdOnly, partitioned);

        var handled = await inbox.HandleAsync();
        var elsewhere = inbox.CopyOf(handled, new Uri("stub://elsewhere"), EnvelopeStatus.Incoming);

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(() =>
            inbox.Store.Inbox.StoreIncomingAsync(elsewhere));

        (await inbox.StatusesOfAsync(handled.Id)).ShouldBe(["Handled"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task under_id_and_destination_the_same_id_handled_at_another_destination_is_another_message(
        bool partitioned)
    {
        await using var inbox = await HandledInbox.StartAsync(MessageIdentity.IdAndDestination, partitioned);

        var handled = await inbox.HandleAsync();
        var elsewhere = inbox.CopyOf(handled, new Uri("stub://elsewhere"), EnvelopeStatus.Incoming);
        var alsoElsewhere = inbox.CopyOf(handled, new Uri("stub://also-elsewhere"), EnvelopeStatus.Incoming);

        await inbox.Store.Inbox.StoreIncomingAsync(elsewhere);
        await inbox.Store.Inbox.StoreIncomingAsync([alsoElsewhere]);

        (await inbox.StatusesOfAsync(handled.Id)).ShouldBe(["Handled", "Incoming", "Incoming"]);
    }

    private sealed class HandledInbox : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly string _schema;

        private HandledInbox(IHost host, string schema)
        {
            _host = host;
            _schema = schema;
            Store = host.GetRuntime().Storage;
        }

        public IMessageStore Store { get; }

        public static async Task<HandledInbox> StartAsync(MessageIdentity identity, bool partitioned)
        {
            var schema = $"handled_identity_{(identity == MessageIdentity.IdOnly ? "id" : "id_dest")}" +
                         (partitioned ? "_partitioned" : "");

            var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
                    opts.Durability.MessageIdentity = identity;
                    opts.Durability.EnableInboxPartitioning = partitioned;

                    // Nothing here may be recovered, released or promoted behind the test's back
                    opts.Durability.DurabilityAgentEnabled = false;
                    opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                    opts.Durability.ScheduledJobPollingTime = 1.Hours();

                    opts.Services.AddResourceSetupOnStartup();
                }).StartAsync(Ct);

            await host.RebuildAllEnvelopeStorageAsync();

            return new HandledInbox(host, schema);
        }

        public Envelope NewEnvelope(Uri destination)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Destination = destination;
            envelope.Status = EnvelopeStatus.Incoming;
            return envelope;
        }

        /// <summary>
        /// Stored, then marked handled: the row KeepAfterMessageHandling retains to refuse a redelivery
        /// </summary>
        public async Task<Envelope> HandleAsync()
        {
            var envelope = NewEnvelope(new Uri("stub://handled"));

            await Store.Inbox.StoreIncomingAsync(envelope);
            await Store.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

            (await StatusesOfAsync(envelope.Id)).ShouldBe(["Handled"], "Guard: the first copy was handled");

            return envelope;
        }

        /// <summary>
        /// What a broker redelivery looks like to the inbox: a new envelope carrying the same id
        /// </summary>
        public Envelope CopyOf(Envelope original, Uri destination, EnvelopeStatus status)
        {
            var copy = NewEnvelope(destination);
            copy.Id = original.Id;
            copy.Status = status;

            if (status == EnvelopeStatus.Scheduled)
            {
                copy.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
            }

            return copy;
        }

        public async Task<List<string>> StatusesOfAsync(Guid id)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync(Ct);

            await using var command = conn.CreateCommand();
            command.CommandText =
                $"select status from {_schema}.{DatabaseConstants.IncomingTable} where id = @id order by status";
            command.Parameters.AddWithValue("id", id);

            var statuses = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                statuses.Add(reader.GetString(0));
            }

            return statuses;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
