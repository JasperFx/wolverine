using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Weasel.Core;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS;

/// <summary>
///     Base class for relational database backed message storage
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract partial class MessageDatabase<T>
{
    public abstract Task<PersistedCounts> FetchCountsAsync();

    public virtual Task DeleteAllHandledAsync()
    {
        throw new NotSupportedException($"This function is not (yet) supported by {GetType().FullNameInCode()}");
    }

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
            var typedConn = (T)conn;
            var lockId = _settings.MigrationLockId;
            var lockAcquired = false;

            try
            {
                // Acquire a global advisory lock to serialize migrations across processes.
                // Without this, concurrent CREATE SCHEMA IF NOT EXISTS statements can race
                // and produce 23505 duplicate-key errors against pg_namespace_nspname_index
                // (or equivalents on other engines). See GH-2518.
                lockAcquired = await acquireMigrationLockAsync(lockId, typedConn, _cancellation);

                await migrateAsync(conn);
            }
            finally
            {
                if (lockAcquired)
                {
                    try
                    {
                        await releaseMigrationLockAsync(lockId, typedConn, _cancellation);
                    }
                    catch
                    {
                        // Best-effort release. The session-scoped lock will be cleaned up
                        // when the connection closes regardless.
                    }
                }

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

    /// <summary>
    /// Attempt to acquire the migration advisory lock with bounded retries.
    /// Returns true if acquired (caller is responsible for the migration and release),
    /// false if not acquired after the retry budget — in which case another process
    /// is presumably finishing the migration; we proceed and let our own SchemaMigration
    /// detect "no changes" as a no-op.
    ///
    /// Providers whose advisory-lock primitive depends on schema that is itself part
    /// of the migration (e.g., SQLite's row-based lock on <c>wolverine_locks</c>)
    /// should override this with a primitive that does not depend on the schema —
    /// for example, SQLite's <c>BEGIN EXCLUSIVE</c>.
    /// </summary>
    protected virtual async Task<bool> acquireMigrationLockAsync(int lockId, T conn, CancellationToken token)
    {
        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (await TryAttainLockAsync(lockId, conn, token))
            {
                return true;
            }

            // Linear backoff: 100ms, 200ms, ..., 1000ms (~5.5s total worst case)
            await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), token);
        }

        return false;
    }

    /// <summary>
    /// Release the lock previously acquired by <see cref="acquireMigrationLockAsync"/>.
    /// Default implementation delegates to the polling-lock release path; providers
    /// that override <see cref="acquireMigrationLockAsync"/> with a different primitive
    /// (e.g., a transaction) must override this too.
    /// </summary>
    protected virtual Task releaseMigrationLockAsync(int lockId, T conn, CancellationToken token)
    {
        return ReleaseLockAsync(lockId, conn, token);
    }

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        return await CreateCommand(
                $"select {DatabaseConstants.IncomingFields} from {QuotedSchemaName}.{DatabaseConstants.IncomingTable}")
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r, _cancellation), _cancellation);
    }

    public Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        return CreateCommand(
                $"select {DatabaseConstants.OutgoingFields} from {QuotedSchemaName}.{DatabaseConstants.OutgoingTable}")
            .FetchListAsync(r => DatabasePersistence.ReadOutgoingAsync(r, _cancellation), _cancellation);
    }

    public Task ReleaseAllOwnershipAsync()
    {
        return CreateCommand(
                $"update {QuotedSchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0;update {QuotedSchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = 0")
            .ExecuteNonQueryAsync(_cancellation);
    }

    public Task ReleaseAllOwnershipAsync(int ownerId)
    {
        return CreateCommand(
                $"update {QuotedSchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0 where owner_id = @id;update {QuotedSchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = 0 where owner_id = @id")
            .With("id", ownerId)
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
            await Migrator.ApplyAllAsync(conn, migration, _settings.AutoCreate, new MigrationLogger(Logger), ct: _cancellation);
        }
    }

    private async Task truncateEnvelopeDataAsync(DbConnection conn)
    {
        try
        {
            var tx = await conn.BeginTransactionAsync(_cancellation);
            await tx.CreateCommand($"delete from {QuotedSchemaName}.{DatabaseConstants.OutgoingTable}")
                .ExecuteNonQueryAsync(_cancellation);
            await tx.CreateCommand($"delete from {QuotedSchemaName}.{DatabaseConstants.IncomingTable}")
                .ExecuteNonQueryAsync(_cancellation);
            await tx.CreateCommand($"delete from {QuotedSchemaName}.{DatabaseConstants.DeadLetterTable}")
                .ExecuteNonQueryAsync(_cancellation);

            if (_settings.Role == MessageStoreRole.Main)
            {
                await tx.CreateCommand($"delete from {QuotedSchemaName}.{DatabaseConstants.AgentRestrictionsTableName}")
                    .ExecuteNonQueryAsync(_cancellation);
                
                await tx.CreateCommand($"delete from {QuotedSchemaName}.{DatabaseConstants.NodeRecordTableName}")
                    .ExecuteNonQueryAsync(_cancellation);
            }

            await tx.CommitAsync(_cancellation);

            await afterTruncateEnvelopeDataAsync(conn);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                "Failure trying to execute the statements to clear envelope storage", e);
        }
    }

    /// <summary>
    /// Hook called after envelope data has been truncated and the transaction committed.
    /// Override to perform provider-specific cleanup such as updating table statistics.
    /// </summary>
    protected virtual Task afterTruncateEnvelopeDataAsync(DbConnection conn)
    {
        return Task.CompletedTask;
    }
}