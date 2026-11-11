using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace MySqlTests;

/// <summary>
/// GH-5428. The expired-handled cleanup (`status = 'Handled' and keep_until &lt;= now`) had no index to use on
/// MySQL, so under REPEATABLE READ it walked the clustered index and X-locked every row it looked at, the
/// Incoming rows live handlers were about to mark Handled among them. A handler commit and the cleanup each
/// held one row the other wanted, and InnoDB dead-lettered the handler's message as the deadlock victim.
/// The cleanup has to leave rows it will not delete alone.
/// </summary>
[Collection("mysql")]
public class Bug_5428_handled_cleanup_locks_live_inbox_rows : IAsyncLifetime
{
    private const string SchemaName = "bug_5428";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;
    private MySqlMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, SchemaName);
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

        theStore = theHost.GetRuntime().Storage.As<MySqlMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private Envelope envelope(EnvelopeStatus status)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = "stub://inbox".ToUri();
        envelope.Status = status;
        envelope.OwnerId = status == EnvelopeStatus.Incoming ? 3 : 0;
        return envelope;
    }

    [Fact]
    public void the_inbox_has_an_index_for_the_cleanup()
    {
        var table = theStore.AllObjects().OfType<Weasel.MySql.Tables.Table>()
            .Single(x => x.Identifier.Name == DatabaseConstants.IncomingTable);

        table.Indexes.Single(x => x.Name == $"idx_{DatabaseConstants.IncomingTable}_keep_until")
            .Columns.ShouldBe([DatabaseConstants.Status, DatabaseConstants.KeepUntil]);
    }

    [Fact]
    public async Task the_cleanup_does_not_wait_on_a_live_incoming_row()
    {
        var expired = envelope(EnvelopeStatus.Handled);
        await theStore.Inbox.StoreIncomingAsync(expired);

        // The steady state of a busy inbox: Handled rows still inside their retention window. They are where
        // the cleanup's range scan stops, so it never reads past the Handled slice into the Incoming rows
        var retained = envelope(EnvelopeStatus.Handled);
        await theStore.Inbox.StoreIncomingAsync(retained);

        var live = envelope(EnvelopeStatus.Incoming);
        await theStore.Inbox.StoreIncomingAsync(live);

        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync(Token);
            await conn.CreateCommand(
                    $"update {SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.KeepUntil} = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 HOUR) where id = '{expired.Id}'")
                .ExecuteNonQueryAsync(Token);
            await conn.CreateCommand(
                    $"update {SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.KeepUntil} = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 1 HOUR) where id = '{retained.Id}'")
                .ExecuteNonQueryAsync(Token);
        }

        // A handler that has started its commit holds its own Incoming row
        await using var handler = new MySqlConnection(Servers.MySqlConnectionString);
        await handler.OpenAsync(Token);
        await using var tx = await handler.BeginTransactionAsync(Token);
        var lockRow = handler.CreateCommand(
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.Attempts} = 1 where id = '{live.Id}'");
        lockRow.Transaction = tx;
        (await lockRow.ExecuteNonQueryAsync(Token)).ShouldBe(1);

        var runtime = theHost.GetRuntime();
        var cleanup = new DeleteExpiredHandledEnvelopesCommand(theStore, runtime.Options.Durability,
                NullLogger.Instance)
            .ExecuteAsync(runtime, Token);

        var finished = await Task.WhenAny(cleanup, Task.Delay(10.Seconds(), Token)) == cleanup;

        await tx.RollbackAsync(Token);
        await cleanup;

        finished.ShouldBeTrue("The expired-handled cleanup waited on a lock held by a live handler's Incoming row");

        var counts = await theStore.Admin.FetchCountsAsync();
        counts.Handled.ShouldBe(1);
        counts.Incoming.ShouldBe(1);
    }
}
