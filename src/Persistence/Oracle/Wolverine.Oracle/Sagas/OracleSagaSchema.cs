using System.Data.Common;
using System.Text.Json;
using JasperFx;
using JasperFx.Core.Reflection;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace Wolverine.Oracle.Sagas;

public class OracleSagaSchema<T, TId> : IDatabaseSagaSchema<TId, T> where T : Saga
{
    private readonly DatabaseSettings _settings;
    private readonly string _insertSql;
    private readonly string _updateSql;
    private readonly string _deleteSql;
    private readonly string _loadSql;

    public OracleSagaSchema(SagaTableDefinition definition, DatabaseSettings settings)
    {
        _settings = settings;
        IdSource = LambdaBuilder.Getter<T, TId>(definition.IdMember);

        var schemaName = (settings.SchemaName ?? "WOLVERINE").ToUpperInvariant();
        var tableName = definition.TableName.ToUpperInvariant();

        _insertSql =
            $"INSERT INTO {schemaName}.{tableName} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.Version}) VALUES (:id, :body, 1)";
        _updateSql =
            $"UPDATE {schemaName}.{tableName} SET {DatabaseConstants.Body} = :body, {DatabaseConstants.Version} = :version + 1, last_modified = SYS_EXTRACT_UTC(SYSTIMESTAMP) WHERE {DatabaseConstants.Id} = :id AND {DatabaseConstants.Version} = :version";
        _loadSql =
            $"SELECT body, version FROM {schemaName}.{tableName} WHERE {DatabaseConstants.Id} = :id";

        _deleteSql = $"DELETE FROM {schemaName}.{tableName} WHERE id = :id";

        var table = new Table(new OracleObjectName(schemaName, tableName));

        // Determine ID column type
        var idType = typeof(TId);
        if (idType == typeof(Guid))
        {
            table.AddColumn<Guid>("id").AsPrimaryKey();
        }
        else if (idType == typeof(int))
        {
            table.AddColumn<int>("id").AsPrimaryKey();
        }
        else if (idType == typeof(long))
        {
            table.AddColumn<long>("id").AsPrimaryKey();
        }
        else if (idType == typeof(string))
        {
            table.AddColumn("id", "VARCHAR2(200)").AsPrimaryKey();
        }
        else
        {
            table.AddColumn<TId>("id").AsPrimaryKey();
        }

        table.AddColumn(DatabaseConstants.Body, "CLOB").NotNull();
        table.AddColumn(DatabaseConstants.Version, "NUMBER(10)").DefaultValue(1).NotNull();
        table.AddColumn<DateTimeOffset>("created").DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)").NotNull();
        table.AddColumn<DateTimeOffset>("last_modified").DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)").NotNull();

        Table = table;
    }

    public void MarkAsChecked() => HasChecked = true;

    public bool HasChecked { get; private set; }

    public async Task EnsureStorageExistsAsync(CancellationToken cancellationToken)
    {
        if (HasChecked || _settings.AutoCreate == AutoCreate.None) return;

        await using var conn = new OracleConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var migration = await SchemaMigration.DetermineAsync(conn, cancellationToken, Table);

        if (migration.Difference != SchemaPatchDifference.None)
        {
            await new OracleMigrator().ApplyAllAsync(conn, migration, _settings.AutoCreate, ct: cancellationToken);
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

        var cmd = ((OracleConnection)transaction.Connection!).CreateCommand(_insertSql, (OracleTransaction)transaction);
        addIdParameter(cmd, "id", id);
        cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Clob) { Value = JsonSerializer.Serialize(saga) });
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        saga.Version = 1;
    }

    public async Task UpdateAsync(T saga, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await EnsureStorageExistsAsync(cancellationToken);

        var id = IdSource(saga);

        var cmd = ((OracleConnection)transaction.Connection!).CreateCommand(_updateSql, (OracleTransaction)transaction);
        cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Clob) { Value = JsonSerializer.Serialize(saga) });
        addIdParameter(cmd, "id", id);
        cmd.With("version", saga.Version);
        var count = await cmd.ExecuteNonQueryAsync(cancellationToken);

        if (count == 0)
            throw new SagaConcurrencyException(
                $"Saga of type {saga.GetType().FullNameInCode()} and id {id} cannot be updated because of optimistic concurrency violations");

        saga.Version++;
    }

    public async Task DeleteAsync(T saga, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await EnsureStorageExistsAsync(cancellationToken);

        var cmd = ((OracleConnection)transaction.Connection!).CreateCommand(_deleteSql, (OracleTransaction)transaction);
        addIdParameter(cmd, "id", IdSource(saga));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T?> LoadAsync(TId id, DbTransaction tx, CancellationToken cancellationToken)
    {
        await EnsureStorageExistsAsync(cancellationToken);

        var cmd = ((OracleConnection)tx.Connection!).CreateCommand(_loadSql, (OracleTransaction)tx);
        addIdParameter(cmd, "id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var body = await reader.GetFieldValueAsync<string>(0, cancellationToken);
        var saga = JsonSerializer.Deserialize<T>(body);
        saga!.Version = Convert.ToInt32(reader.GetValue(1));

        await reader.CloseAsync();

        return saga;
    }

    private static void addIdParameter(OracleCommand cmd, string name, object? id)
    {
        if (id is Guid guidValue)
        {
            cmd.Parameters.Add(new OracleParameter(name, OracleDbType.Raw) { Value = guidValue.ToByteArray() });
        }
        else
        {
            cmd.With(name, id!);
        }
    }
}
