using IntegrationTests;
using Microsoft.Data.SqlClient;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;

namespace SqlServerTests.Transport;

public class external_message_tables : ExternalTableTransportCompliance
{
    public external_message_tables(ITestOutputHelper output) : base(output)
    {
    }

    protected override string connectionString => Servers.SqlServerConnectionString;
    protected override string idColumnType => "uniqueidentifier";
    protected override string bodyColumnType => "varbinary(max)";
    protected override string timestampColumnType => "datetimeoffset";
    protected override string messageTypeColumnType => "varchar(250)";

    protected override void configurePersistence(WolverineOptions opts, string connectionString, string schemaName) => 
        opts.UseSqlServerPersistenceAndTransport(connectionString, schemaName);

    protected override async ValueTask dropSchemaAsync(string connectionString, string[] schemas, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var schema in schemas)
        {
            await conn.DropSchemaAsync(schema, cancellationToken);
        }
    }
}
