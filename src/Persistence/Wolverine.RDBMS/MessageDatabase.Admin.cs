using System.Data.Common;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
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

    public Task MigrateAsync()
    {
        return MigrateAsync(null);
    }

    public async Task MigrateAsync(AutoCreate? overrideAutoCreate)
    {
        var autoCreate = overrideAutoCreate ?? _settings.AutoCreate;

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

                await migrateAsync(conn, autoCreate);
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

    public async Task AssertStorageExistsAsync(CancellationToken token)
    {
        await using var conn = await DataSource.OpenConnectionAsync(token);
        try
        {
            // A reachable database is not enough — the schema/tables can be missing (e.g. never
            // provisioned, or dropped). Reuse the same Weasel diff the migration path uses so that
            // 'resources check' reports an un-provisioned schema as unhealthy.
            var migration = await SchemaMigration.DetermineAsync(conn, token, Objects);
            if (migration.Difference != SchemaPatchDifference.None)
            {
                throw new InvalidOperationException(
                    $"The Wolverine message storage for database '{Name}' is missing or out of date (schema difference: {migration.Difference}). Run 'resources setup' or enable auto-provisioning.");
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private Task migrateAsync(DbConnection conn)
    {
        return migrateAsync(conn, _settings.AutoCreate);
    }

    private async Task migrateAsync(DbConnection conn, AutoCreate autoCreate)
    {
        var migration = await SchemaMigration.DetermineAsync(conn, _cancellation, Objects);

        if (migration.Difference != SchemaPatchDifference.None)
        {
            if (autoCreate == AutoCreate.None)
            {
                Logger.LogWarning(
                    "Message storage in database {Database} is out of date ({Difference}) but AutoCreate is None — no migration applied. Run 'resources setup' to provision it",
                    Name, migration.Difference);
                return;
            }

            await Migrator.ApplyAllAsync(conn, migration, autoCreate, new MigrationLogger(Logger), ct: _cancellation);
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

                // Clear the dynamic-listener registry too, so test hosts that call
                // ResetResourceState / ClearAllAsync get a truly clean slate. Only
                // attempted when the registry is opted in, since otherwise the
                // wolverine_listeners table doesn't exist.
                if (Durability.EnableDynamicListeners)
                {
                    await tx.CreateCommand($"delete from {QuotedSchemaName}.{DatabaseConstants.ListenersTableName}")
                        .ExecuteNonQueryAsync(_cancellation);
                }
            }

            // Let a provider clear its own extra tables inside the SAME reset transaction so
            // nothing is left behind. This is deliberately a per-provider hook rather than a
            // blanket loop over every table registered via AddTable: some providers register
            // tables through that same path that a reset must preserve (e.g. SQL Server's
            // rate-limit table). The PostgreSQL store overrides this to also empty the queue
            // transport's queue + scheduled tables.
            await truncateAdditionalTablesAsync(tx, _cancellation);

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

    /// <summary>
    /// Hook to clear additional provider-specific tables within the reset transaction started by
    /// <see cref="ClearAllAsync"/> / <see cref="RebuildAsync"/>. The default is a no-op. Providers
    /// whose transport registers extra tables that a reset should also empty override this — e.g.
    /// the PostgreSQL queue transport registers its queue and scheduled-message tables on the store
    /// and clears them here. This is deliberately a per-provider decision rather than a blanket loop
    /// over every table registered via <c>AddTable</c>: some providers register tables through that
    /// same path that a reset must keep intact (for example SQL Server's rate-limit table). Runs
    /// inside the reset transaction so the whole reset stays atomic.
    /// </summary>
    protected virtual Task truncateAdditionalTablesAsync(DbTransaction tx, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}