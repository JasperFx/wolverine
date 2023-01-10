using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Weasel.Core;
using Wolverine.Logging;

namespace Wolverine.RDBMS;

/// <summary>
///     Base class for relational database backed message storage
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract partial class MessageDatabase<T>
{
    public async Task ClearAllAsync()
    {
        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(_cancellation);
        await truncateEnvelopeDataAsync(conn);
    }
    
    public async Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType)
    {
        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        var sql = $"update {Settings.SchemaName}.{DatabaseConstants.DeadLetterTable} set {DatabaseConstants.Replayable} = @replay";

        if (!string.IsNullOrEmpty(exceptionType))
        {
            sql = $"{sql} where {DatabaseConstants.ExceptionType} = @extype";   
        }

        return await conn.CreateCommand(sql).With("replay", true).With("extype", exceptionType)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task RebuildAsync()
    {
        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        await migrateAsync(conn);

        await truncateEnvelopeDataAsync(conn);
    }

    public async Task MigrateAsync()
    {
        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        await migrateAsync(conn);
    }

    public abstract Task<PersistedCounts> FetchCountsAsync();

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        return await conn
            .CreateCommand(
                $"select {DatabaseConstants.IncomingFields} from {Settings.SchemaName}.{DatabaseConstants.IncomingTable}")
            .FetchList(r => DatabasePersistence.ReadIncomingAsync(r, _cancellation), _cancellation);
    }

    public async Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        return await conn
            .CreateCommand(
                $"select {DatabaseConstants.OutgoingFields} from {Settings.SchemaName}.{DatabaseConstants.OutgoingTable}")
            .FetchList(r => DatabasePersistence.ReadOutgoingAsync(r, _cancellation), _cancellation);
    }

    public async Task ReleaseAllOwnershipAsync()
    {
        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        await conn.CreateCommand(
                $"update {Settings.SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0;update {Settings.SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = 0")
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task CheckConnectivityAsync(CancellationToken token)
    {
        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(token);
        await conn.CloseAsync();
    }

    private async Task migrateAsync(DbConnection conn)
    {
        var migration = await SchemaMigration.Determine(conn, Objects);

        if (migration.Difference != SchemaPatchDifference.None)
        {
            await Migrator.ApplyAll(conn, migration, AutoCreate.CreateOrUpdate);
        }
    }

    private async Task truncateEnvelopeDataAsync(DbConnection conn)
    {
        try
        {
            var tx = await conn.BeginTransactionAsync(_cancellation);
            await tx.CreateCommand($"delete from {Settings.SchemaName}.{DatabaseConstants.OutgoingTable}")
                .ExecuteNonQueryAsync(_cancellation);
            await tx.CreateCommand($"delete from {Settings.SchemaName}.{DatabaseConstants.IncomingTable}")
                .ExecuteNonQueryAsync(_cancellation);
            await tx.CreateCommand($"delete from {Settings.SchemaName}.{DatabaseConstants.DeadLetterTable}")
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