using IntegrationTests;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.Oracle;

namespace OracleTests;

/// <summary>
/// GH-5172. The Weasel db-list / db-dump / db-patch / db-assert / db-apply commands find their databases through
/// every registered IDatabaseSource. After PersistMessagesWithOracle() that source has to report the Oracle
/// message store, the same as it does for PostgreSQL, SQL Server, MySQL and SQLite.
/// </summary>
[Collection("oracle")]
public class weasel_database_discovery_5172 : IAsyncLifetime
{
    private const string SchemaName = "WEASEL_DISCOVERY";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    public ValueTask InitializeAsync()
    {
        // Built, but not started -- which is all the db-* commands do
        theHost = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, SchemaName);

                // Same shape as the issue: nothing is built at startup, the schema is
                // managed through the Weasel commands instead
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            }).Build();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        theHost.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<IDatabase> theOnlyDatabase()
    {
        var databases = new List<IDatabase>();
        foreach (var source in theHost.Services.GetServices<IDatabaseSource>())
        {
            databases.AddRange(await source.BuildDatabases());
        }

        return databases.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task the_oracle_message_store_is_discovered()
    {
        var database = await theOnlyDatabase();

        database.Describe().Engine.ShouldBe("Oracle");
        database.Describe().DatabaseName.ShouldBe(SchemaName);
        database.AllSchemaNames().ShouldContain(SchemaName);
    }

    [Fact]
    public async Task describe_databases_reports_the_oracle_store()
    {
        var source = theHost.Services.GetServices<IDatabaseSource>().ShouldHaveSingleItem();
        var usage = await source.DescribeDatabasesAsync(Token);

        usage.MainDatabase.ShouldNotBeNull().Engine.ShouldBe("Oracle");
    }

    [Fact]
    public async Task can_dump_the_creation_script()
    {
        var database = await theOnlyDatabase();

        // What db-dump writes
        var sql = database.ToDatabaseScript();

        sql.ShouldContain($"{SchemaName}.WOLVERINE_INCOMING_ENVELOPES", Case.Insensitive);
        sql.ShouldContain($"{SchemaName}.WOLVERINE_OUTGOING_ENVELOPES", Case.Insensitive);
        sql.ShouldContain($"{SchemaName}.WOLVERINE_DEAD_LETTERS", Case.Insensitive);
        sql.ShouldContain($"{SchemaName}.WOLVERINE_NODES", Case.Insensitive);
    }

    [Fact]
    public async Task can_apply_then_assert_the_schema()
    {
        var database = await theOnlyDatabase();

        // What db-apply and then db-assert do. A clean assert right after the apply also
        // proves the configured tables do not report drift against what Oracle reads back
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: Token);
        await database.AssertDatabaseMatchesConfigurationAsync(Token);

        // And db-patch would have nothing left to write
        var migration = await database.CreateMigrationAsync(Token);
        migration.Difference.ShouldBe(SchemaPatchDifference.None);
    }
}
