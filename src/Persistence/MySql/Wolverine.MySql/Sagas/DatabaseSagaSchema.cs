using System.Data.Common;
using System.Text.Json;
using JasperFx;
using JasperFx.Core.Reflection;
using MySqlConnector;
using Weasel.Core;
using Weasel.MySql;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace Wolverine.MySql.Sagas;

public class DatabaseSagaSchema<T, TId> : IDatabaseSagaSchema<TId, T> where T : Saga
{
    private readonly DatabaseSettings _settings;
    private readonly string _insertSql;
    private readonly string _updateSql;
    private readonly string _deleteSql;
    private readonly string _loadSql;

    public DatabaseSagaSchema(SagaTableDefinition definition, DatabaseSettings settings)
    {
        _settings = settings;
        IdSource = LambdaBuilder.Getter<T, TId>(definition.IdMember);

        _insertSql =
            $"INSERT INTO {settings.SchemaName}.{definition.TableName} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.Version}) VALUES (@id, @body, 1)";
        _updateSql =
            $"UPDATE {settings.SchemaName}.{definition.TableName} SET {DatabaseConstants.Body} = @body, {DatabaseConstants.Version} = @version + 1, last_modified = UTC_TIMESTAMP(6) WHERE {DatabaseConstants.Id} = @id AND {DatabaseConstants.Version} = @version";
        _loadSql =
            $"SELECT body, version FROM {settings.SchemaName}.{definition.TableName} WHERE {DatabaseConstants.Id} = @id";

        _deleteSql = $"DELETE FROM {settings.SchemaName}.{definition.TableName} WHERE id = @id";

        var table = new Table(new DbObjectName(settings.SchemaName, definition.TableName));
        table.AddColumn<TId>("id").AsPrimaryKey();
        table.AddColumn(DatabaseConstants.Body, "JSON").NotNull();
        table.AddColumn(DatabaseConstants.Version, "INT").DefaultValue(1).NotNull();
        table.AddColumn<DateTimeOffset>("created").DefaultValueByExpression("(UTC_TIMESTAMP(6))").NotNull();
        table.AddColumn<DateTimeOffset>("last_modified").DefaultValueByExpression("(UTC_TIMESTAMP(6))").NotNull();

        Table = table;
    }

    public void MarkAsChecked() => HasChecked = true;

    public bool HasChecked { get; private set; }

    public async Task EnsureStorageExistsAsync(CancellationToken cancellationToken)
    {
        if (HasChecked || _settings.AutoCreate == AutoCreate.None) return;

        await using var conn = _settings.DataSource?.CreateConnection() as MySqlConnection ??
                               new MySqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var migration = await SchemaMigration.DetermineAsync(conn, cancellationToken, Table);

        if (migration.Difference != SchemaPatchDifference.None)
        {
            await new MySqlMigrator().ApplyAllAsync(conn, migration, _settings.AutoCreate, ct: cancellationToken);
        }

        HasChecked = true;
    }

    public ISchemaObject Table { get; }

    public Func<T, TId> IdSource { get; }

    public async Task InsertAsync(T saga, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var id = IdSource(saga);
        if (id == null || id.Equals(default(TId)))
            throw new ArgumentOutOfRangeException(nameof(saga),
                "You must define the saga id when using the lightweight saga storage");

        await EnsureStorageExistsAsync(cancellationToken);

        var cmd = (MySqlCommand)transaction.Connection!.CreateCommand();
        cmd.Transaction = (MySqlTransaction)transaction;
        cmd.CommandText = _insertSql;
        cmd.Parameters.AddWithValue("@id", id);
        // MySQL JSON columns require string input, not binary
        cmd.Parameters.AddWithValue("@body", JsonSerializer.Serialize(saga));
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        saga.Version = 1;
    }

    public async Task UpdateAsync(T saga, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await EnsureStorageExistsAsync(cancellationToken);

        var id = IdSource(saga);

        var cmd = (MySqlCommand)transaction.Connection!.CreateCommand();
        cmd.Transaction = (MySqlTransaction)transaction;
        cmd.CommandText = _updateSql;
        // MySQL JSON columns require string input, not binary
        cmd.Parameters.AddWithValue("@body", JsonSerializer.Serialize(saga));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@version", saga.Version);
        var count = await cmd.ExecuteNonQueryAsync(cancellationToken);

        if (count == 0)
            throw new SagaConcurrencyException(
                $"Saga of type {saga.GetType().FullNameInCode()} and id {id} cannot be updated because of optimistic concurrency violations");

        saga.Version++;
    }

    public async Task DeleteAsync(T saga, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await EnsureStorageExistsAsync(cancellationToken);

        var cmd = (MySqlCommand)transaction.Connection!.CreateCommand();
        cmd.Transaction = (MySqlTransaction)transaction;
        cmd.CommandText = _deleteSql;
        cmd.Parameters.AddWithValue("@id", IdSource(saga));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T?> LoadAsync(TId id, DbTransaction tx, CancellationToken cancellationToken)
    {
        await EnsureStorageExistsAsync(cancellationToken);

        var cmd = (MySqlCommand)tx.Connection!.CreateCommand();
        cmd.Transaction = (MySqlTransaction)tx;
        cmd.CommandText = _loadSql;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        // MySQL JSON columns return string data
        var body = await reader.GetFieldValueAsync<string>(0, cancellationToken);
        var saga = JsonSerializer.Deserialize<T>(body);
        saga!.Version = await reader.GetFieldValueAsync<int>(1, cancellationToken);

        await reader.CloseAsync();

        return saga;
    }
}
