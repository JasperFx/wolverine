using Microsoft.Data.Sqlite;
using Weasel.Sqlite;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;

namespace SqliteTests.Transport;

public class external_message_tables : ExternalTableTransportCompliance
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(external_message_tables));

    public external_message_tables(ITestOutputHelper output) : base(output)
    {
    }

    protected override string connectionString => _database.ConnectionString;
    protected override string idColumnType => "TEXT";
    protected override string bodyColumnType => "TEXT";
    protected override string timestampColumnType => "TEXT";
    protected override string messageTypeColumnType => "TEXT";

    // schemaName is deliberately ignored: SQLite has no schemas, so Wolverine folds a configured
    // schema name into a table-name prefix instead (see TablePrefixing/GH-3943). Leaving it unset
    // keeps the store on the default "main", which is the "no prefix at all" value.
    protected override void configurePersistence(WolverineOptions opts, string connectionString, string schemaName) =>
        opts.UseSqlitePersistenceAndTransport(connectionString);

    protected override async ValueTask dropSchemaAsync(string connectionString, string[] schemas, CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var schema in schemas)
        {
            foreach (var table in await conn.ExistingTablesAsync($"{schema}_", ct: cancellationToken))
            {
                await using var cmd = conn.CreateCommand($"DROP TABLE {table.QualifiedName}");
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await conn.CloseAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
