using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.Transport;

public class resource_setup_against_a_missing_database : PostgresqlContext, IAsyncLifetime
{
    private const string DatabaseName = "wolverine_fresh_provisioning";
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = DatabaseName
        }.ConnectionString;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)", conn);
        await drop.ExecuteNonQueryAsync();
        await conn.CloseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task setup_resources_provisions_the_transport_onto_a_database_created_by_a_resource_creator()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UsePostgresqlPersistenceAndTransport(_connectionString, schema: "fresh",
                    transportSchema: "fresh_queues");
                opts.ListenToPostgresqlQueue("incoming");

                // Stand-in for any database-building registration (EF Core's tenanted DbContext
                // initializer, or an application-owned creator). IResourceCreator services are
                // executed before any IStatefulResource.Setup(), so this is the documented way to
                // make the target database exist before the transport or message store touch it.
                opts.Services.AddSingleton<IResourceCreator>(new DatabaseCreator(DatabaseName));
            })
            .Build();

        // The whole point: resource discovery + setup must succeed while the target database
        // does not exist yet. Before the discovery seam, FindResources() threw
        // BrokerInitializationException out of PostgresqlTransport's eager connectivity probe
        // before DatabaseCreator ever ran.
        await host.SetupResources();

        (await tableExistsAsync("fresh_queues", "wolverine_queue_incoming")).ShouldBeTrue();
        (await tableExistsAsync("fresh", DatabaseConstants.IncomingTable)).ShouldBeTrue();

        await host.StartAsync();
        await host.StopAsync();
    }

    private async Task<bool> tableExistsAsync(string schema, string table)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var tables = await conn.ExistingTablesAsync(schemas: [schema]);
        await conn.CloseAsync();

        return tables.Any(x => x.Name == table);
    }

    private class DatabaseCreator : IResourceCreator
    {
        private readonly string _databaseName;

        public DatabaseCreator(string databaseName)
        {
            _databaseName = databaseName;
        }

        public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync(cancellationToken);

            await using var exists =
                new NpgsqlCommand("select 1 from pg_database where datname = @name", conn);
            exists.Parameters.AddWithValue("name", _databaseName);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", conn);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await conn.CloseAsync();
        }

        public Task Check(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task ClearState(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task Teardown(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task Setup(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task<IRenderable> DetermineStatus(CancellationToken token)
        {
            return Task.FromResult<IRenderable>(new Markup("Database creator"));
        }

        public string Type => "database";
        public string Name => _databaseName;
        public Uri SubjectUri => new("wolverine://database-creator");
        public Uri ResourceUri => new($"postgresql://localhost/{_databaseName}");
    }
}
