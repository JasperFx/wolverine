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
        
        var builder = ToCommandBuilder();
        foreach (var assignment in tenantConnectionStrings.AllActiveByTenant())
        {
            builder.Append($"delete from  {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} where {StorageConstants.TenantIdColumn} = ");
            builder.AppendParameter(assignment.TenantId);
            builder.Append($";insert into {Settings.SchemaName}.{DatabaseConstants.TenantsTableName} ({StorageConstants.TenantIdColumn}, {StorageConstants.ConnectionStringColumn}) values (");
            builder.AppendParameter(assignment.TenantId);
            builder.Append(", ");
            builder.AppendParameter(assignment.Value);
            builder.Append(");");
        }

        var command = builder.Compile();
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

        string value = null;
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
            
            return value;
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
                        $"select {StorageConstants.TenantIdColumn}, {StorageConstants.ConnectionStringColumn} from {Settings.SchemaName}.{DatabaseConstants.TenantsTableName}")
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
}