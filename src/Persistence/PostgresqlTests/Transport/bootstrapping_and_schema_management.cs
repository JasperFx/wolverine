using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PersistenceTests;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Xunit.Abstractions;

namespace PostgresqlTests.Transport;

public class bootstrapping_and_schema_management : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    public bootstrapping_and_schema_management(ITestOutputHelper output)
    {
        _output = output;
    }

    private IHost theHost;

    public async Task InitializeAsync()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("transports");
        await conn.CloseAsync();
        
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "transports");
                opts.ListenToPostgresqlQueue("one");
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task expected_tables_exist_for_queue()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var names = await conn.ExistingTablesAsync(schemas: ["transports"]);

        await conn.CloseAsync();
        
        names.Any(x => x.QualifiedName == "transports.wolverine_queue_one").ShouldBeTrue();
        names.Any(x => x.QualifiedName == "transports.wolverine_queue_one_scheduled").ShouldBeTrue();
    }
}