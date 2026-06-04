using JasperFx;
using JasperFx.MultiTenancy;
using Weasel.Core;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    private bool _hasAppliedDefaults;

    public async Task SeedDatabasesAsync(ITenantedSource<string> tenantConnectionStrings)
    {
        // Gotta make sure the database state is in good shape first...
        // TODO -- might have to latch this w/ AutoCreate
        await ApplyAllConfiguredChangesToDatabaseAsync(ct: _cancellation);

        var assignments = tenantConnectionStrings.AllActiveByTenant().ToArray();
        if (assignments.Length == 0)
        {
            // Nothing to seed. An empty master tenant table is valid — tenants may be registered
            // later at runtime (e.g. via the master-table tenancy source). Compiling/executing a
            // command with no SQL appended throws "CommandText property has not been initialized".
            // GH-3019.
            return;
        }

        var builder = ToCommandBuilder();
        foreach (var assignment in assignments)
        {
            builder.Append($"delete from  {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} where {StorageConstants.TenantIdColumn} = ");
            builder.AppendParameter(assignment.TenantId);
            builder.Append($";insert into {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} ({StorageConstants.TenantIdColumn}, {StorageConstants.ConnectionStringColumn}) values (");
            builder.AppendParameter(assignment.TenantId);
            builder.Append(", ");
            builder.AppendParameter(assignment.Value);
            builder.Append(");");
        }

        await using var command = builder.Compile();
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);
        try
        {
            command.Connection = conn;
            await command.ExecuteNonQueryAsync(_cancellation);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
    
    public async Task<string> TryFindTenantConnectionString(string tenantId)
    {
        if (!_hasAppliedDefaults && _settings.AutoCreate != AutoCreate.None && _settings.TenantConnections != null)
        {
            await SeedDatabasesAsync(_settings.TenantConnections);
            _hasAppliedDefaults = true;
        }

        string? value = null;
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        try
        {
            await using var reader =
                await conn.CreateCommand(
                        $"select {StorageConstants.ConnectionStringColumn} from {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} where {StorageConstants.TenantIdColumn} = @id")
                    .With("id", tenantId)
                    .ExecuteReaderAsync(_cancellation);

            if (await reader.ReadAsync(_cancellation))
            {
                value = await reader.GetFieldValueAsync<string>(0, _cancellation);
            }

            await reader.CloseAsync();
            
            return value!;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<Assignment<string>>> LoadAllTenantConnectionStrings()
    {
        if (!_hasAppliedDefaults && _settings.AutoCreate != AutoCreate.None && _settings.TenantConnections != null)
        {
            await SeedDatabasesAsync(_settings.TenantConnections);
            _hasAppliedDefaults = true;
        }
        
        var list = new List<Assignment<string>>();
        
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        try
        {
            await using var reader =
                await conn.CreateCommand(
                        $"select {StorageConstants.TenantIdColumn}, {StorageConstants.ConnectionStringColumn} from {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} where {DatabaseConstants.DisabledColumn} = @disabled")
                    .With("disabled", false)
                    .ExecuteReaderAsync(_cancellation);

            while (await reader.ReadAsync(_cancellation))
            {
                var tenantId = await reader.GetFieldValueAsync<string>(0);
                var connectionString = await reader.GetFieldValueAsync<string>(1);
                
                list.Add(new Assignment<string>(tenantId, connectionString));
            }

            await reader.CloseAsync();

        }
        finally
        {
            await conn.CloseAsync();
        }

        return list;
    }

    public IDatabaseProvider Provider { get; }

    public async Task AddTenantRecordAsync(string tenantId, string connectionString)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);
        try
        {
            // Upsert + (re-)enable. Uses a portable delete-then-insert rather than PostgreSQL's
            // ON CONFLICT (which SqlServer rejects), and a bool parameter rather than a `false`
            // literal (SqlServer has no boolean literal — it parses `false` as a column name). GH-3023.
            await conn.CreateCommand(
                    $"delete from {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} where {StorageConstants.TenantIdColumn} = @id;" +
                    $"insert into {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} ({StorageConstants.TenantIdColumn}, {StorageConstants.ConnectionStringColumn}, {DatabaseConstants.DisabledColumn}) values (@id, @connection, @disabled)")
                .With("id", tenantId)
                .With("connection", connectionString)
                .With("disabled", false)
                .ExecuteNonQueryAsync(_cancellation);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task SetTenantDisabledAsync(string tenantId, bool disabled)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);
        try
        {
            await conn.CreateCommand(
                    $"update {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} set {DatabaseConstants.DisabledColumn} = @disabled where {StorageConstants.TenantIdColumn} = @id")
                .With("id", tenantId)
                .With("disabled", disabled)
                .ExecuteNonQueryAsync(_cancellation);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task DeleteTenantRecordAsync(string tenantId)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);
        try
        {
            await conn.CreateCommand(
                    $"delete from {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} where {StorageConstants.TenantIdColumn} = @id")
                .With("id", tenantId)
                .ExecuteNonQueryAsync(_cancellation);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<string>> LoadDisabledTenantIdsAsync()
    {
        var list = new List<string>();
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);
        try
        {
            await using var reader = await conn.CreateCommand(
                    $"select {StorageConstants.TenantIdColumn} from {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} where {DatabaseConstants.DisabledColumn} = @disabled")
                .With("disabled", true)
                .ExecuteReaderAsync(_cancellation);

            while (await reader.ReadAsync(_cancellation))
            {
                list.Add(await reader.GetFieldValueAsync<string>(0, _cancellation));
            }

            await reader.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }

        return list;
    }
}