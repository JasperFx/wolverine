using IntegrationTests;
using Npgsql;
using NSubstitute;
using Shouldly;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace PostgresqlTests.Bugs;

/// <summary>
/// R1 (durable compliance rig). The documented outbox pattern opens a connection, begins a transaction, enlists a
/// <see cref="DatabaseEnvelopeTransaction" />, commits and disposes its transaction and connection. Returning the
/// <see cref="MessageContext" /> to its pool later disposed that enlisted transaction, and with it a
/// <c>DbTransaction</c> its owner had already finished. Npgsql reuses one transaction object per pooled physical
/// connection, so that late dispose killed the LIVE transaction of whichever handler had rented the connection in
/// the meantime: its commit threw and its message was dead lettered. Wolverine never owned that transaction.
/// </summary>
public class an_enlisted_transaction_is_not_disposed_by_the_pooled_context
{
    [Fact]
    public async Task returning_the_context_to_the_pool_leaves_the_next_renters_transaction_alone()
    {
        var token = TestContext.Current.CancellationToken;

        // One physical connection in a pool of its own, so the second "handler" is certain to get the connector
        // the first one released
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            MaxPoolSize = 1,
            ApplicationName = "r1_" + Guid.NewGuid().ToString("N")[..8]
        };
        await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);

        var context = new MessageContext(Substitute.For<IWolverineRuntime>());

        // The first handler: the documented pattern, all the way through to disposing its own connection
        var first = await dataSource.OpenConnectionAsync(token);
        var firstTx = await first.BeginTransactionAsync(token);
        await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(Substitute.For<IMessageDatabase>(), firstTx));
        await firstTx.CommitAsync(token);
        await firstTx.DisposeAsync();
        await first.DisposeAsync();

        // The second handler rents the same physical connection and is part way through its own transaction
        await using var second = await dataSource.OpenConnectionAsync(token);
        await using var secondTx = await second.BeginTransactionAsync(token);
        await using (var cmd = second.CreateCommand())
        {
            cmd.Transaction = secondTx;
            cmd.CommandText = "select 1";
            await cmd.ExecuteScalarAsync(token);
        }

        // ...when the first handler's context goes back to the pool
        context.ClearState();

        await Should.NotThrowAsync(() => secondTx.CommitAsync(token));
    }
}
