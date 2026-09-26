using IntegrationTests;
using MySqlConnector;
using Weasel.MySql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;

namespace MySqlTests.Transport;

public class external_message_tables : ExternalTableTransportCompliance
{
    public external_message_tables(ITestOutputHelper output) : base(output)
    {
    }

    protected override string connectionString => Servers.MySqlConnectionString;
    protected override string idColumnType => "CHAR(36)";
    protected override string bodyColumnType => "LONGBLOB";
    protected override string timestampColumnType => "DATETIME";
    protected override string messageTypeColumnType => "VARCHAR(255)";

    protected override void configurePersistence(WolverineOptions opts, string connectionString, string schemaName) => 
        opts.UseMySqlPersistenceAndTransport(connectionString, schemaName);

    protected override async ValueTask dropSchemaAsync(string connectionString, string[] schemas, CancellationToken cancellationToken)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var schema in schemas)
        {
            await conn.DropSchemaAsync(schema, cancellationToken);
        }
    }
}
