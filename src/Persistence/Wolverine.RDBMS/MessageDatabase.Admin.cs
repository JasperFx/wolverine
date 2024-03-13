using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.Logging;

namespace Wolverine.RDBMS;

/// <summary>
///     Base class for relational database backed message storage
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract partial class MessageDatabase<T>
{
    public abstract Task<PersistedCounts> FetchCountsAsync();

    public async Task ClearAllAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync(_cancellation);
        try
        {
            await truncateEnvelopeDataAsync(conn);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType)
    {
        var sql =
            $"update {SchemaName}.{DatabaseConstants.DeadLetterTable} set {DatabaseConstants.Replayable} = @replay";

        if (!string.IsNullOrEmpty(exceptionType))
        {
            sql = $"{sql} where {DatabaseConstants.ExceptionType} = @extype";
        }

        return await CreateCommand(sql).With("replay", true).With("extype", exceptionType)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task RebuildAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync(_cancellation);

        try
        {
            await migrateAsync(conn);

            await truncateEnvelopeDataAsync(conn);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task MigrateAsync()
    {
        Func<Task> tryMigrate = async () =>
        {
            await using var conn = await DataSource.OpenConnectionAsync(_cancellation);

            try
            {
                await migrateAsync(conn);
            }
            finally
            {
                await conn.CloseAsync();
            }
        };

        try
        {
            await tryMigrate();
        }
        catch (TaskCanceledException)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            await Task.Delay(new Random().Next(250, 1000).Milliseconds(), _cancellation);
            await tryMigrate();
        }
    }

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        return await CreateCommand(
                $"select {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable}")
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r, _cancellation), _cancellation);
    }

    public Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        return CreateCommand(
                $"select {DatabaseConstants.OutgoingFields} from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .FetchListAsync(r => DatabasePersistence.ReadOutgoingAsync(r, _cancellation), _cancellation);
    }

    public Task ReleaseAllOwnershipAsync()
    {
        return CreateCommand(
                $"update {SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0;update {SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = 0")
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task CheckConnectivityAsync(CancellationToken token)
    {
        await using var conn = await DataSource.OpenConnectionAsync(token);
        await conn.CloseAsync();
    }

    private async Task migrateAsync(DbConnection conn)
    {
        var migration = await SchemaMigration.DetermineAsync(conn, _cancellation, Objects);

        if (migration.Difference != SchemaPatchDifference.None)
        {
            await Migrator.ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate, ct: _cancellation);
        }
    }

    private async Task truncateEnvelopeDataAsync(DbConnection conn)
    {
        try
        {
            var tx = await conn.BeginTransactionAsync(_cancellation);
            await tx.CreateCommand($"delete from {SchemaName}.{DatabaseConstants.OutgoingTable}")
                .ExecuteNonQueryAsync(_cancellation);
            await tx.CreateCommand($"delete from {SchemaName}.{DatabaseConstants.IncomingTable}")
                .ExecuteNonQueryAsync(_cancellation);
            await tx.CreateCommand($"delete from {SchemaName}.{DatabaseConstants.DeadLetterTable}")
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