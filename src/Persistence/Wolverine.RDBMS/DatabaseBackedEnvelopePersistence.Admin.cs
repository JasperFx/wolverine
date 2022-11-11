using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Logging;
using Weasel.Core;

namespace Wolverine.RDBMS;

/// <summary>
/// Base class for relational database backed message storage
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract partial class DatabaseBackedMessageStore<T>
{
    public async Task ClearAllAsync()
    {
        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync(_cancellation);
        await truncateEnvelopeDataAsync(conn);
    }

    public async Task RebuildAsync()
    {
        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        await migrateAsync(conn);

        await truncateEnvelopeDataAsync(conn);
    }

    public async Task MigrateAsync()
    {
        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        await migrateAsync(conn);
    }

    private async Task migrateAsync(DbConnection conn)
    {
        var migration = await SchemaMigration.Determine(conn, Objects);

        if (migration.Difference != SchemaPatchDifference.None)
        {
            await Migrator.ApplyAll(conn, migration, AutoCreate.CreateOrUpdate);
        }
    }

    public abstract Task<PersistedCounts> FetchCountsAsync();

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        return await conn
            .CreateCommand(
                $"select {DatabaseConstants.IncomingFields} from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable}")
            .FetchList(r => DatabasePersistence.ReadIncomingAsync(r, _cancellation), _cancellation);
    }

    public async Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        return await conn
            .CreateCommand(
                $"select {DatabaseConstants.OutgoingFields} from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}")
            .FetchList(r => DatabasePersistence.ReadOutgoingAsync(r, _cancellation), _cancellation);
    }

    public async Task ReleaseAllOwnershipAsync()
    {
        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        await conn.CreateCommand(
                $"update {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0;update {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = 0")
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task CheckConnectivityAsync(CancellationToken token)
    {
        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync(token);
        await conn.CloseAsync();
    }

    private async Task truncateEnvelopeDataAsync(DbConnection conn)
    {
        try
        {
            var tx = await conn.BeginTransactionAsync(_cancellation);
            await tx.CreateCommand($"delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}")
                .ExecuteNonQueryAsync(_cancellation);
            await tx.CreateCommand($"delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable}")
                .ExecuteNonQueryAsync(_cancellation);
            await tx.CreateCommand($"delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.DeadLetterTable}")
                .ExecuteNonQueryAsync(_cancellation);

            await tx.CommitAsync(_cancellation);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                "Failure trying to execute the statements to clear envelope storage", e);
        }
    }
}
