using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Tracking;

namespace SqliteTests;

public class message_store_initialization_and_configuration : SqliteContext, IAsyncLifetime
{
    private IHost _host;
    private readonly string _connectionString;
    private readonly SqliteTestDatabase _database;

    public message_store_initialization_and_configuration()
    {
        _database = Servers.CreateDatabase(nameof(message_store_initialization_and_configuration));
        _connectionString = _database.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_connectionString);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _database.Dispose();
    }

    [Fact]
    public void main_store_role()
    {
        _host.GetRuntime().Storage.Role.ShouldBe(MessageStoreRole.Main);
    }

    [Fact]
    public async Task builds_the_node_and_control_queue_tables()
    {
        using var dataSource = new SqliteDataSource(_connectionString);
        await using var conn = (SqliteConnection)await dataSource.OpenConnectionAsync();

        var tables = await conn.ExistingTablesAsync(schemas: ["main"]);

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
            .Database.ShouldBeOfType<SqliteMessageStore>()
            .Settings.SchemaName.ShouldBe("main");
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
            ConnectionString = _connectionString,
            Role = MessageStoreRole.Tenant,
            SchemaName = "main"
        };

        var dataSource = new SqliteDataSource(_connectionString);
        var store = new SqliteMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<SqliteMessageStore>.Instance);

        // Should delete itself at the end
        var nodes = await store.Nodes.LoadAllNodesAsync(CancellationToken.None);
        nodes.Any().ShouldBeFalse();
    }
}
