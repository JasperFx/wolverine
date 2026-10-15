using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace MartenTests;

/// <summary>
/// GH-5016. Marten under DurabilityMode.ServerlessWithOutbox: outgoing envelopes are persisted in the Marten
/// session's transaction as owned by any node (0), sent inline as soon as that transaction commits with their
/// outbox rows deleted, and anything left behind is recovered by a durability pass.
/// </summary>
public class serverless_with_outbox_5016 : PostgresqlContext, IAsyncLifetime
{
    private const string SchemaName = "serverless_marten";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ServerlessOutboxHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<ServerlessOutboxCascaded>().To("stub://serverless-cascaded");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private IReadOnlyList<Envelope> sent()
    {
        return theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>()
            .Endpoints["serverless-cascaded"].Sent.ToArray();
    }

    private async Task<Dictionary<Guid, int>> outboxRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);

        var rows = new Dictionary<Guid, int>();
        await using var reader = await conn
            .CreateCommand($"select id, owner_id from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            rows[await reader.GetFieldValueAsync<Guid>(0, Token)] = await reader.GetFieldValueAsync<int>(1, Token);
        }

        return rows;
    }

    [Fact]
    public async Task cascaded_messages_are_sent_inline_when_the_handler_transaction_commits()
    {
        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeAsync(new ServerlessOutboxCommand(id), Token);

        // The handler's document was committed...
        await using (var query = theHost.DocumentStore().QuerySession())
        {
            (await query.LoadAsync<Item>(id, Token)).ShouldNotBeNull();
        }

        // ...and its cascaded message went out before InvokeAsync() returned, with nothing left behind
        sent().Single().Message.ShouldBe(new ServerlessOutboxCascaded(id));
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_marten_outbox_sends_inline_when_the_session_commits()
    {
        var id = Guid.NewGuid();

        using (var scope = theHost.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            outbox.Enroll(session);

            session.Store(new Item { Id = id });
            await outbox.PublishAsync(new ServerlessOutboxCascaded(id));

            await session.SaveChangesAsync(Token);
        }

        sent().Single().Message.ShouldBe(new ServerlessOutboxCascaded(id));
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_unsent_row_from_a_marten_session_is_owned_by_any_node_and_recovered_by_a_durability_pass()
    {
        var envelope = new Envelope(new ServerlessOutboxCascaded(Guid.NewGuid()))
        {
            Destination = "stub://serverless-cascaded".ToUri(),
            ContentType = "application/json"
        };
        envelope.Data = theHost.GetRuntime().Options.DefaultSerializer.Write(envelope);

        // Persisted through the Marten session's outbox, but never sent, as when the request died after the
        // commit and before the inline send
        await using (var session = theHost.DocumentStore().LightweightSession())
        {
            var context = new MessageContext(theHost.GetRuntime());
            await new MartenEnvelopeTransaction(session, context).PersistOutgoingAsync(envelope);
            await session.SaveChangesAsync(Token);
        }

        (await outboxRowsAsync())[envelope.Id].ShouldBe(0);

        // Still within OutboxStaleTime, so a pass leaves it to the request that may still be sending it
        var early = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        early.OutgoingSent.ShouldBe(0);
        sent().ShouldBeEmpty();

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Token);
            await conn.CreateCommand(
                    $"update {SchemaName}.{DatabaseConstants.OutgoingTable} set \"timestamp\" = :time where id = :id")
                .With("time", DateTimeOffset.UtcNow.Subtract(2.Hours()))
                .With("id", envelope.Id)
                .ExecuteNonQueryAsync(Token);
        }

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(1);
        report.Stores.Single(x => x.StoreUri == theHost.GetRuntime().Storage.As<PostgresqlMessageStore>().Uri)
            .Lock.ShouldBe(DurabilityPassLockStatus.Acquired);

        sent().Single().Id.ShouldBe(envelope.Id);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }
}

public record ServerlessOutboxCommand(Guid Id);

public record ServerlessOutboxCascaded(Guid Id);

public static class ServerlessOutboxHandler
{
    public static ServerlessOutboxCascaded Handle(ServerlessOutboxCommand command, IDocumentSession session)
    {
        session.Store(new Item { Id = command.Id });
        return new ServerlessOutboxCascaded(command.Id);
    }
}
