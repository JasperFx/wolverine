using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;
using Wolverine.Transports.Tcp;

namespace SqliteTests;

/// <summary>
/// GH-4216. The whole shared suite under <see cref="MessageIdentity.IdAndDestination"/> rather than the
/// default <see cref="MessageIdentity.IdOnly"/>. Only PostgreSQL, SQL Server and RavenDb answered it under
/// this shape before, which left SQLite's composite-key handling covered by exactly one DDL test
/// (<see cref="Bug_2680_message_identity_id_and_destination_emits_invalid_ddl"/>) and nothing at all on the
/// read and write paths that have to match on <c>(id, received_at)</c>.
///
/// GH-4209 is the reason it matters: that defect was identity-shape specific end to end -- matching on
/// <c>id</c> alone where the key is <c>(id, received_at)</c> -- and a store that never runs the suite under
/// the composite shape cannot report that class of bug.
/// </summary>
[Collection("sqlite")]
public class SqliteMessageStore_with_IdAndDestination_Identity : MessageStoreCompliance, IAsyncLifetime
{
    // Its own file, so the composite-key DDL never shares a database with the default-identity suite.
    private readonly SqliteTestDatabase _database =
        Servers.CreateDatabase(nameof(SqliteMessageStore_with_IdAndDestination_Identity));

    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString);

                opts.ListenAtPort(2345).UseDurableInbox();
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
            }).StartAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
