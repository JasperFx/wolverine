using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;

namespace PostgresqlTests;

public class message_store_initialization_and_configuration : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        await dropSchema();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "registry");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task builds_the_node_and_control_queue_tables()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var tables = await conn.ExistingTablesAsync(schemas: ["registry"]);
        await conn.CloseAsync();

        tables.ShouldContain(x => x.Name == DatabaseConstants.NodeTableName);
        tables.ShouldContain(x => x.Name == DatabaseConstants.NodeAssignmentsTableName);
        tables.ShouldContain(x => x.Name == DatabaseConstants.ControlQueueTableName);
    }

    [Fact]
    public void should_set_the_control_endpoint_and_add_transport()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var expectedUri = $"dbcontrol://{runtime.Options.UniqueNodeId}".ToUri();
        runtime.Options.Transports.NodeControlEndpoint!.Uri.ShouldBe(expectedUri);

        runtime.Options.Transports.OfType<DatabaseControlTransport>().Single()
            .Database.ShouldBeOfType<PostgresqlMessageStore>()
            .Settings.SchemaName.ShouldBe("registry");
    }

    [Fact]
    public async Task stores_the_current_node_on_startup()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var nodes = await runtime.Storage.Nodes.LoadAllNodesAsync(CancellationToken.None);

        var current = nodes.Single();

        current.NodeId.ShouldBe(runtime.Options.UniqueNodeId);
        current.ControlUri.ShouldBe(runtime.Options.Transports.NodeControlEndpoint.Uri);

    }

    [Fact]
    public async Task deletes_the_node_on_shutdown()
    {
        await _host.StopAsync();
        _host.Dispose();
        _host = null;

        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.PostgresConnectionString,
            IsMaster = false,
            SchemaName = "registry"
        };

        var store = new PostgresqlMessageStore(settings, new DurabilitySettings(), NpgsqlDataSource.Create(Servers.PostgresConnectionString),
            NullLogger<PostgresqlMessageStore>.Instance);

        // Should delete itself at the end
        var nodes = await store.Nodes.LoadAllNodesAsync(CancellationToken.None);
        nodes.Any().ShouldBeFalse();
    }
}