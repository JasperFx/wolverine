using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Sqlite;

namespace SqliteTests;

// End-to-end coverage for the dbcontrol:// endpoint mode lock.
//
// Even when the user installs the broadest policies the framework offers —
// UseDurableInboxOnAllListeners() and UseDurableOutboxOnAllSendingEndpoints() —
// the control endpoint must stay BufferedInMemory. Marking it Durable would
// force every leader-election / agent-reassignment envelope through the same
// store-backed inbox/outbox the durability agent itself owns, which deadlocks.
//
// The unit-level tests in PersistenceTests pin down the Mode setter contract.
// This test sits one layer up and proves the framework's built-in policies
// silently skip the control endpoint via SupportsMode, rather than throwing.
public class control_endpoint_policy_resistance : SqliteContext, IAsyncLifetime
{
    private IHost _host = null!;
    private SqliteTestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = Servers.CreateDatabase(nameof(control_endpoint_policy_resistance));

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString);

                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                opts.Policies.UseDurableLocalQueues();

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

        _database?.Dispose();
    }

    [Fact]
    public void global_durable_policies_do_not_promote_the_dbcontrol_endpoint()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var control = runtime.Options.Transports.NodeControlEndpoint!;
        control.ShouldBeOfType<DatabaseControlEndpoint>();
        control.Mode.ShouldBe(EndpointMode.BufferedInMemory,
            "UseDurableInboxOnAllListeners + UseDurableOutboxOnAllSendingEndpoints must " +
            "skip the dbcontrol:// endpoint — promoting it to Durable would deadlock the " +
            "durability layer (the control queue lives on the same store).");
    }

    [Fact]
    public void all_dbcontrol_endpoints_remain_buffered_after_policies()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var dbControlEndpoints = runtime.Options.Transports
            .OfType<DatabaseControlTransport>()
            .SelectMany(t => t.Endpoints())
            .OfType<DatabaseControlEndpoint>()
            .ToArray();

        dbControlEndpoints.ShouldNotBeEmpty();
        foreach (var endpoint in dbControlEndpoints)
        {
            endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        }
    }
}
