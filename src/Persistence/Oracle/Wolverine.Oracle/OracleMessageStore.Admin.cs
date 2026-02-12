using JasperFx;
using JasperFx.Core;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Oracle;
using Wolverine.Logging;
using Wolverine.Oracle.Util;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore
{
    // IMessageStoreAdmin
    public async Task ClearAllAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);

        var tables = new List<string>
        {
            DatabaseConstants.IncomingTable,
            DatabaseConstants.OutgoingTable,
            DatabaseConstants.DeadLetterTable
        };

        if (_settings.Role == MessageStoreRole.Main)
        {
            tables.Add(DatabaseConstants.AgentRestrictionsTableName);
            tables.Add(DatabaseConstants.NodeRecordTableName);
        }

        foreach (var tableName in tables)
        {
            try
            {
                var cmd = conn.CreateCommand($"DELETE FROM {SchemaName}.{tableName}");
                await cmd.ExecuteNonQueryAsync(_cancellation);
            }
            catch (OracleException e) when (e.Number == 942)
            {
                // Table doesn't exist yet, ignore
            }
        }

        await conn.CloseAsync();
    }

    public async Task RebuildAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);

        // Drop all tables first (CASCADE CONSTRAINTS handles FK dependencies)
        var objects = AllObjects().ToArray();
        foreach (var obj in objects.Reverse())
        {
            try
            {
                var dropCmd = conn.CreateCommand(
                    $"DROP TABLE {obj.Identifier.QualifiedName} CASCADE CONSTRAINTS");
                await dropCmd.ExecuteNonQueryAsync(_cancellation);
            }
            catch (OracleException e) when (e.Number == 942) // ORA-00942: table or view does not exist
            {
                // OK - table doesn't exist yet
            }
        }

        // Also drop the lock table
        try
        {
            var dropLocks = conn.CreateCommand(
                $"DROP TABLE {SchemaName}.{Schema.LockTable.TableName} CASCADE CONSTRAINTS");
            await dropLocks.ExecuteNonQueryAsync(_cancellation);
        }
        catch (OracleException e) when (e.Number == 942)
        {
            // OK
        }

        // Now recreate all tables
        foreach (var obj in objects)
        {
            var migration = await SchemaMigration.DetermineAsync(conn, _cancellation, obj);
            if (migration.Difference != SchemaPatchDifference.None)
            {
                try
                {
                    await new OracleMigrator().ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate, ct: _cancellation);
                }
                catch (OracleException e) when (e.Number is 955 or 2275 or 1408)
                {
                    // ORA-00955: name already used by existing object
                    // ORA-02275: referential constraint already exists
                    // ORA-01408: such column list already indexed
                }
            }
        }

        await conn.CloseAsync();
    }

    public async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);

        // Incoming counts by status
        var statusCmd = conn.CreateCommand(
            $"SELECT status, COUNT(*) FROM {SchemaName}.{DatabaseConstants.IncomingTable} GROUP BY status");
        await using (var reader = await statusCmd.ExecuteReaderAsync(_cancellation))
        {
            while (await reader.ReadAsync(_cancellation))
            {
                var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0, _cancellation));
                var count = await reader.GetFieldValueAsync<decimal>(1, _cancellation);

                if (status == EnvelopeStatus.Incoming) counts.Incoming = (int)count;
                else if (status == EnvelopeStatus.Handled) counts.Handled = (int)count;
                else if (status == EnvelopeStatus.Scheduled) counts.Scheduled = (int)count;
            }
        }

        // Outgoing count
        var outCmd = conn.CreateCommand(
            $"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.OutgoingTable}");
        var outCount = await outCmd.ExecuteScalarAsync(_cancellation);
        counts.Outgoing = Convert.ToInt32(outCount);

        // Dead letter count
        var deadCmd = conn.CreateCommand(
            $"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.DeadLetterTable}");
        var deadCount = await deadCmd.ExecuteScalarAsync(_cancellation);
        counts.DeadLetter = Convert.ToInt32(deadCount);

        await conn.CloseAsync();
        return counts;
    }

    public async Task MigrateAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);

        var objects = AllObjects().ToArray();
        foreach (var obj in objects)
        {
            var migration = await SchemaMigration.DetermineAsync(conn, _cancellation, obj);
            if (migration.Difference != SchemaPatchDifference.None)
            {
                try
                {
                    await new OracleMigrator().ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate, ct: _cancellation);
                }
                catch (OracleException e) when (e.Number is 955 or 2275 or 1408)
                {
                    // ORA-00955: name already used by existing object
                    // ORA-02275: referential constraint already exists
                    // ORA-01408: such column list already indexed
                }
            }
        }

        await conn.CloseAsync();
    }

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
            $"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable}");

        var list = await cmd.FetchListAsync(
            r => OracleEnvelopeReader.ReadIncomingAsync(r), _cancellation);
        await conn.CloseAsync();
        return list;
    }

    public async Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
            $"SELECT {DatabaseConstants.OutgoingFields} FROM {SchemaName}.{DatabaseConstants.OutgoingTable}");

        var list = await cmd.FetchListAsync(
            r => OracleEnvelopeReader.ReadOutgoingAsync(r), _cancellation);
        await conn.CloseAsync();
        return list;
    }

    public async Task ReleaseAllOwnershipAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);

        var inCmd = conn.CreateCommand(
            $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET {DatabaseConstants.OwnerId} = 0");
        await inCmd.ExecuteNonQueryAsync(_cancellation);

        var outCmd = conn.CreateCommand(
            $"UPDATE {SchemaName}.{DatabaseConstants.OutgoingTable} SET {DatabaseConstants.OwnerId} = 0");
        await outCmd.ExecuteNonQueryAsync(_cancellation);

        await conn.CloseAsync();
    }

    public async Task ReleaseAllOwnershipAsync(int ownerId)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);

        var inCmd = conn.CreateCommand(
            $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET {DatabaseConstants.OwnerId} = 0 WHERE {DatabaseConstants.OwnerId} = :ownerId");
        inCmd.With("ownerId", ownerId);
        await inCmd.ExecuteNonQueryAsync(_cancellation);

        var outCmd = conn.CreateCommand(
            $"UPDATE {SchemaName}.{DatabaseConstants.OutgoingTable} SET {DatabaseConstants.OwnerId} = 0 WHERE {DatabaseConstants.OwnerId} = :ownerId");
        outCmd.With("ownerId", ownerId);
        await outCmd.ExecuteNonQueryAsync(_cancellation);

        await conn.CloseAsync();
    }

    public async Task CheckConnectivityAsync(CancellationToken token)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(token);
        var cmd = conn.CreateCommand("SELECT 1 FROM DUAL");
        await cmd.ExecuteScalarAsync(token);
        await conn.CloseAsync();
    }

    public async Task DeleteAllHandledAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync(CancellationToken.None);

        var deleted = 1;

        var sql = $@"DELETE FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id IN (
    SELECT id FROM {SchemaName}.{DatabaseConstants.IncomingTable}
    WHERE status = '{EnvelopeStatus.Handled}'
    ORDER BY id
    FETCH FIRST 10000 ROWS ONLY
    FOR UPDATE SKIP LOCKED)";

        try
        {
            while (deleted > 0)
            {
                var cmd = conn.CreateCommand(sql);
                deleted = await cmd.ExecuteNonQueryAsync();
                await Task.Delay(10.Milliseconds());
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
