using Fisher;
using JasperFx;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Fisher.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace FisherTests;

/// <summary>
/// GH-5016. Fisher under DurabilityMode.ServerlessWithOutbox: outgoing envelopes are persisted in the Fisher
/// session's transaction as owned by any node (0), sent inline as soon as that transaction commits with their
/// outbox rows deleted, and anything left behind is recovered by a durability pass. The Fisher twin of
/// <c>PolecatTests.serverless_with_outbox_5016</c>.
/// </summary>
public class serverless_with_outbox_5016 : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase(nameof(serverless_with_outbox_5016));

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ServerlessFisherOutboxHandler));

                opts.Services.AddFisher(o =>
                    {
                        o.Connection(theDatabase.ConnectionString);
                        o.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<ServerlessFisherOutboxCascaded>().To("stub://serverless-fisher-cascaded");
            }).StartAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    private IReadOnlyList<Envelope> sent()
    {
        return theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>()
            .Endpoints["serverless-fisher-cascaded"].Sent.ToArray();
    }

    private async Task<Dictionary<Guid, int>> outboxRowsAsync()
    {
        await using var conn = new SqliteConnection(theDatabase.ConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select id, owner_id from {DatabaseConstants.OutgoingTable}";

        var rows = new Dictionary<Guid, int>();
        await using var reader = await cmd.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows[Guid.Parse(reader.GetString(0))] = reader.GetInt32(1);
        }

        return rows;
    }

    [Fact]
    public async Task cascaded_messages_are_sent_inline_when_the_handler_transaction_commits()
    {
        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeAsync(new ServerlessFisherOutboxCommand(id), Token);

        // The handler's document was committed...
        await using (var query = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession())
        {
            (await query.LoadAsync<ServerlessFisherItem>(id, Token)).ShouldNotBeNull();
        }

        // ...and its cascaded message went out before InvokeAsync() returned, with nothing left behind
        sent().Single().Message.ShouldBe(new ServerlessFisherOutboxCascaded(id));
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_outboxed_session_sends_inline_when_it_commits()
    {
        var id = Guid.NewGuid();

        var context = new MessageContext(theHost.GetRuntime());
        await using (var session = theHost.Services.GetRequiredService<OutboxedSessionFactory>().OpenSession(context))
        {
            session.Store(new ServerlessFisherItem { Id = id });
            await context.PublishAsync(new ServerlessFisherOutboxCascaded(id));

            await session.SaveChangesAsync(Token);
        }

        sent().Single().Message.ShouldBe(new ServerlessFisherOutboxCascaded(id));
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_unsent_row_from_a_fisher_session_is_owned_by_any_node_and_recovered_by_a_durability_pass()
    {
        var envelope = new Envelope(new ServerlessFisherOutboxCascaded(Guid.NewGuid()))
        {
            Destination = "stub://serverless-fisher-cascaded".ToUri(),
            ContentType = "application/json"
        };
        envelope.Data = theHost.GetRuntime().Options.DefaultSerializer.Write(envelope);

        // Persisted through the Fisher session's outbox, but never sent, as when the request died after the
        // commit and before the inline send
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            var context = new MessageContext(theHost.GetRuntime());
            await new FisherEnvelopeTransaction(session, context).PersistOutgoingAsync(envelope);
            await session.SaveChangesAsync(Token);
        }

        (await outboxRowsAsync())[envelope.Id].ShouldBe(0);

        // Still within OutboxStaleTime, so a pass leaves it to the request that may still be sending it
        var early = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        early.OutgoingSent.ShouldBe(0);
        sent().ShouldBeEmpty();

        await using (var conn = new SqliteConnection(theDatabase.ConnectionString))
        {
            await conn.OpenAsync(Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"update {DatabaseConstants.OutgoingTable} set \"timestamp\" = datetime('now', '-2 hours') where lower(id) = '{envelope.Id:D}'";
            await cmd.ExecuteNonQueryAsync(Token);
        }

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(1);
        report.Stores.Single().Lock.ShouldBe(DurabilityPassLockStatus.Acquired);

        sent().Single().Id.ShouldBe(envelope.Id);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }
}

public record ServerlessFisherOutboxCommand(Guid Id);

public record ServerlessFisherOutboxCascaded(Guid Id);

public class ServerlessFisherItem
{
    public Guid Id { get; set; }
}

public static class ServerlessFisherOutboxHandler
{
    public static ServerlessFisherOutboxCascaded Handle(ServerlessFisherOutboxCommand command, IDocumentSession session)
    {
        session.Store(new ServerlessFisherItem { Id = command.Id });
        return new ServerlessFisherOutboxCascaded(command.Id);
    }
}
