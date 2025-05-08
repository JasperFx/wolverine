using System.Data.Common;
using System.Text.Json;
using JasperFx;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace Wolverine.SqlServer.Sagas;

public class DatabaseSagaSchema<TId, TSaga> : IDatabaseSagaSchema<TId, TSaga> where TSaga : Saga
{
    private readonly DatabaseSettings _settings;
    private readonly string _insertSql;
    private readonly string _updateSql;
    private readonly string _deleteSql;
    private readonly string _loadSql;

    public DatabaseSagaSchema(SagaTableDefinition definition, DatabaseSettings settings)
    {
        _settings = settings;
        IdSource = LambdaBuilder.Getter<TSaga, TId>(definition.IdMember);

        _insertSql = $"insert into {settings.SchemaName}.{definition.TableName} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.Version}) values (@id, @body, 1)";
        _updateSql =
            $"update {settings.SchemaName}.{definition.TableName} set {DatabaseConstants.Body} = @body, {DatabaseConstants.Version} = @version + 1, last_modified = SYSDATETIMEOFFSET() where {DatabaseConstants.Id} = @id and {DatabaseConstants.Version} = @version";
        _loadSql = $"select body, version from {settings.SchemaName}.{definition.TableName} where {DatabaseConstants.Id} = @id";

        _deleteSql = $"delete from {settings.SchemaName}.{definition.TableName} where id = @id";
        
        var table = new Table(new DbObjectName(settings.SchemaName, definition.TableName));
        table.AddColumn<TId>("id").AsPrimaryKey();
        table.AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();
        table.AddColumn(DatabaseConstants.Version, "int").DefaultValue(1).NotNull();
        table.AddColumn<DateTimeOffset>("created").DefaultValueByExpression("SYSDATETIMEOFFSET()").NotNull();
        table.AddColumn<DateTimeOffset>("last_modified").DefaultValueByExpression("SYSDATETIMEOFFSET()").NotNull();

        Table = table;
    }

    public void MarkAsChecked() => HasChecked = true;

    public bool HasChecked { get; private set; }
    
    private async Task ensureStorageExistsAsync(CancellationToken cancellationToken)
    {
        if (HasChecked || _settings.AutoCreate == AutoCreate.None) return;

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var migration = await SchemaMigration.DetermineAsync(conn, cancellationToken, Table);

        if (migration.Difference != SchemaPatchDifference.None)
        {
            await new SqlServerMigrator().ApplyAllAsync(conn, migration, _settings.AutoCreate, ct: cancellationToken);
        }

        HasChecked = true;
    }

    public ISchemaObject Table { get; }

    public Func<TSaga,TId> IdSource { get; }

    public async Task InsertAsync(TSaga saga, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var id = IdSource(saga);
        if (id == null || id.Equals(default(TId))) throw new ArgumentOutOfRangeException(nameof(saga), "You must define the saga id when using the lightweight saga storage");
        
        await ensureStorageExistsAsync(cancellationToken);
        await transaction.CreateCommand(_insertSql)
            .With("id", id)
            .With("body", JsonSerializer.SerializeToUtf8Bytes(saga))
            .ExecuteNonQueryAsync(cancellationToken);

        saga.Version = 1;
    }
    
    public async Task UpdateAsync(TSaga saga, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await ensureStorageExistsAsync(cancellationToken);

        var id = IdSource(saga);
        var count = await transaction.CreateCommand(_updateSql)
            .With("id", id)
            .With("body", JsonSerializer.SerializeToUtf8Bytes(saga))
            .With("version", saga.Version)
            .ExecuteNonQueryAsync(cancellationToken);

        if (count == 0)
            throw new SagaConcurrencyException(
                $"Saga of type {saga.GetType().FullNameInCode()} and id {id} cannot be updated because of optimistic concurrency violations");
        
        saga.Version++;
    }

    public async Task DeleteAsync(TSaga saga, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await ensureStorageExistsAsync(cancellationToken);
        await transaction
            .CreateCommand(_deleteSql)
            .With("id", IdSource(saga))
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TSaga?> LoadAsync(TId id, DbTransaction tx, CancellationToken cancellationToken)
    {
        await ensureStorageExistsAsync(cancellationToken);
        await using var reader = await tx.CreateCommand(_loadSql)
            .With("id", id)
            .ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var body = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken);
        var saga = JsonSerializer.Deserialize<TSaga>(body);
        saga!.Version = await reader.GetFieldValueAsync<int>(1, cancellationToken);

        await reader.CloseAsync();

        return saga;

    }
}