using System.Data.Common;
using IntegrationTests;
using Weasel.Core;
using Weasel.Oracle;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;

namespace OracleTests.Transport;

public class external_message_tables : ExternalTableTransportCompliance
{
	public external_message_tables(ITestOutputHelper output) : base(output)
	{
	}

	protected override string connectionString => Servers.OracleConnectionString;
	protected override string idColumnType => "RAW(16)";
	protected override string bodyColumnType => "BLOB";
	protected override string timestampColumnType => "TIMESTAMP WITH TIME ZONE";
	protected override string messageTypeColumnType => "VARCHAR2(4000)";

	protected override void configurePersistence(WolverineOptions opts, string connectionString, string schemaName) =>
		opts.PersistMessagesWithOracle(connectionString, schemaName);

	protected override Task<SchemaMigration> determineMigrationAsync(DbConnection conn, CancellationToken token, ITable table)
	{
		var builder = new OracleMigrator().CreateCommandBuilder(conn);
		return SchemaMigration.DetermineAsync(conn, builder, token, table);
	}

	protected override async ValueTask dropSchemaAsync(string connectionString, string[] schemas, CancellationToken cancellationToken)
	{
		var dataSource = new OracleDataSource(connectionString);
		await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
		foreach (var schema in schemas)
		{
			await conn.DropSchemaAsync(schema, cancellationToken);
		}
	}
}