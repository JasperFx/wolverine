using System.Data.Common;
using System.Text.Json;
using JasperFx;
using JasperFx.Core.Reflection;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace Wolverine.Sqlite.Sagas;

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

        var idColumn = typeof(TId) == typeof(Guid) || typeof(TId) == typeof(string) ? "TEXT" : "INTEGER";

        _insertSql = $"insert into {definition.TableName} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.Version}) values (@id, @body, 1)";
        _updateSql =
            $"update {definition.TableName} set {DatabaseConstants.Body} = @body, {DatabaseConstants.Version} = @version + 1, last_modified = datetime('now') where {DatabaseConstants.Id} = @id and {DatabaseConstants.Version} = @version";
        _loadSql = $"select body, version from {definition.TableName} where {DatabaseConstants.Id} = @id";

        _deleteSql = $"delete from {definition.TableName} where id = @id";

        var table = new Table(new SqliteObjectName(definition.TableName));
        table.AddColumn("id", idColumn).AsPrimaryKey();
        table.AddColumn(DatabaseConstants.Body, "TEXT").NotNull();
        table.AddColumn(DatabaseConstants.Version, "INTEGER").DefaultValue(1).NotNull();
        table.AddColumn("created", "TEXT").NotNull().DefaultValueByExpression("(datetime('now'))");
        table.AddColumn("last_modified", "TEXT").NotNull().DefaultValueByExpression("(datetime('now'))");

        Table = table;
    }

    public void MarkAsChecked() => HasChecked = true;

    public bool HasChecked { get; private set; }

    private async Task ensureStorageExistsAsync(DbTransaction? tx, CancellationToken cancellationToken)
    {
        if (HasChecked || _settings.AutoCreate == AutoCreate.None) return;

        if (tx?.Connection is SqliteConnection)
        {
            var table = (Table)Table;
            var tableExists = await tx.CreateCommand("select count(*) from sqlite_master where type = 'table' and name = @name")
                .With("name", table.Identifier.Name)
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);

            if (Convert.ToInt32(tableExists) == 0)
            {
                var createSql = table.ToBasicCreateTableSql();

                await tx.CreateCommand(createSql)
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            HasChecked = true;
            return;
        }

        SqliteConnection? ownedConnection = null;

        try
        {
            var dataSource = _settings.DataSource;
            if (dataSource == null)
            {
                var connectionString = _settings.ConnectionString
                                       ?? throw new InvalidOperationException("Either DataSource or ConnectionString is required");

                dataSource = _settings.DataSource = new WolverineSqliteDataSource(connectionString);
            }

            ownedConnection = (SqliteConnection)await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var conn = ownedConnection;
            var migration = await SchemaMigration.DetermineAsync(conn, cancellationToken, Table);

            if (migration.Difference != SchemaPatchDifference.None)
            {
                await new SqliteMigrator().ApplyAllAsync(conn, migration, _settings.AutoCreate, ct: cancellationToken);
            }

            HasChecked = true;
        }
        finally
        {
            if (ownedConnection != null)
            {
                await ownedConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public Func<T, TId> IdSource { get; }
    public ISchemaObject Table { get; }

    public async Task InsertAsync(T document, DbTransaction tx, CancellationToken cancellationToken)
    {
        await ensureStorageExistsAsync(tx, cancellationToken);

        var json = JsonSerializer.Serialize(document);
        var id = IdSource(document);

        if (id == null || EqualityComparer<TId>.Default.Equals(id, default!))
        {
            throw new InvalidOperationException("Saga id cannot be null or empty");
        }

        await tx.CreateCommand(_insertSql)
            .With("id", id.ToString()!)
            .With("body", json)
            .ExecuteNonQueryAsync(cancellationToken);

        document.Version = 1;
    }

    public async Task<T?> LoadAsync(TId id, DbTransaction tx, CancellationToken cancellationToken)
    {
        await ensureStorageExistsAsync(tx, cancellationToken);

        var cmd = tx.CreateCommand(_loadSql)
            .With("id", id?.ToString() ?? throw new InvalidOperationException("Saga id cannot be null"));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var body = await reader.GetFieldValueAsync<string>(0, cancellationToken);
        var version = await reader.GetFieldValueAsync<long>(1, cancellationToken);

        var document = JsonSerializer.Deserialize<T>(body);
        if (document != null)
        {
            document.Version = (int)version;
        }

        return document;
    }

    public async Task UpdateAsync(T document, DbTransaction tx, CancellationToken cancellationToken)
    {
        await ensureStorageExistsAsync(tx, cancellationToken);

        var json = JsonSerializer.Serialize(document);
        var id = IdSource(document);

        var count = await tx.CreateCommand(_updateSql)
            .With("id", id?.ToString() ?? throw new InvalidOperationException("Saga id cannot be null"))
            .With("body", json)
            .With("version", document.Version)
            .ExecuteNonQueryAsync(cancellationToken);

        if (count == 0)
        {
            throw new Exception(
                $"Saga version mismatch for {typeof(T).FullName} with id {id}. Possible concurrent update detected.");
        }

        document.Version++;
    }

    public async Task DeleteAsync(T document, DbTransaction tx, CancellationToken cancellationToken)
    {
        await ensureStorageExistsAsync(tx, cancellationToken);

        var id = IdSource(document);

        await tx.CreateCommand(_deleteSql)
            .With("id", id?.ToString() ?? throw new InvalidOperationException("Saga id cannot be null"))
            .ExecuteNonQueryAsync(cancellationToken);
    }
}
