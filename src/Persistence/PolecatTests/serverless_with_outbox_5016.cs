using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Polecat;
using Wolverine.Polecat.Publishing;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace PolecatTests;

/// <summary>
/// GH-5016. Polecat under DurabilityMode.ServerlessWithOutbox: outgoing envelopes are persisted in the Polecat
/// session's transaction as owned by any node (0), sent inline as soon as that transaction commits with their
/// outbox rows deleted, and anything left behind is recovered by a durability pass. The Polecat twin of
/// <c>MartenTests.serverless_with_outbox_5016</c>.
/// </summary>
public class serverless_with_outbox_5016 : IAsyncLifetime
{
    private const string SchemaName = "serverless_polecat";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ServerlessPolecatOutboxHandler));

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = SchemaName;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<ServerlessPolecatOutboxCascaded>().To("stub://serverless-polecat-cascaded");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

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
            .Endpoints["serverless-polecat-cascaded"].Sent.ToArray();
    }

    private async Task<Dictionary<Guid, int>> outboxRowsAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
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

        await theHost.MessageBus().InvokeAsync(new ServerlessPolecatOutboxCommand(id), Token);

        // The handler's document was committed...
        await using (var query = theHost.DocumentStore().QuerySession())
        {
            (await query.LoadAsync<ServerlessPolecatItem>(id, Token)).ShouldNotBeNull();
        }

        // ...and its cascaded message went out before InvokeAsync() returned, with nothing left behind
        sent().Single().Message.ShouldBe(new ServerlessPolecatOutboxCascaded(id));
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_outboxed_session_sends_inline_when_it_commits()
    {
        var id = Guid.NewGuid();

        var context = new MessageContext(theHost.GetRuntime());
        await using (var session = theHost.Services.GetRequiredService<OutboxedSessionFactory>().OpenSession(context))
        {
            session.Store(new ServerlessPolecatItem { Id = id });
            await context.PublishAsync(new ServerlessPolecatOutboxCascaded(id));

            await session.SaveChangesAsync(Token);
        }

        sent().Single().Message.ShouldBe(new ServerlessPolecatOutboxCascaded(id));
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_unsent_row_from_a_polecat_session_is_owned_by_any_node_and_recovered_by_a_durability_pass()
    {
        var envelope = new Envelope(new ServerlessPolecatOutboxCascaded(Guid.NewGuid()))
        {
            Destination = "stub://serverless-polecat-cascaded".ToUri(),
            ContentType = "application/json"
        };
        envelope.Data = theHost.GetRuntime().Options.DefaultSerializer.Write(envelope);

        // Persisted through the Polecat session's outbox, but never sent, as when the request died after the
        // commit and before the inline send
        await using (var session = theHost.DocumentStore().LightweightSession())
        {
            var context = new MessageContext(theHost.GetRuntime());
            await new PolecatEnvelopeTransaction(session, context).PersistOutgoingAsync(envelope);
            await session.SaveChangesAsync(Token);
        }

        (await outboxRowsAsync())[envelope.Id].ShouldBe(0);

        // Still within OutboxStaleTime, so a pass leaves it to the request that may still be sending it
        var early = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        early.OutgoingSent.ShouldBe(0);
        sent().ShouldBeEmpty();

        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(Token);
            await conn.CreateCommand(
                    $"update {SchemaName}.{DatabaseConstants.OutgoingTable} set \"timestamp\" = @time where id = @id")
                .With("time", DateTimeOffset.UtcNow.Subtract(2.Hours()))
                .With("id", envelope.Id)
                .ExecuteNonQueryAsync(Token);
        }

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(1);
        report.Stores.Single(x => x.StoreUri == theHost.GetRuntime().Storage.As<SqlServerMessageStore>().Uri)
            .Lock.ShouldBe(DurabilityPassLockStatus.Acquired);

        sent().Single().Id.ShouldBe(envelope.Id);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }
}

public record ServerlessPolecatOutboxCommand(Guid Id);

public record ServerlessPolecatOutboxCascaded(Guid Id);

public class ServerlessPolecatItem
{
    public Guid Id { get; set; }
}

public static class ServerlessPolecatOutboxHandler
{
    public static ServerlessPolecatOutboxCascaded Handle(ServerlessPolecatOutboxCommand command, IDocumentSession session)
    {
        session.Store(new ServerlessPolecatItem { Id = command.Id });
        return new ServerlessPolecatOutboxCascaded(command.Id);
    }
}
