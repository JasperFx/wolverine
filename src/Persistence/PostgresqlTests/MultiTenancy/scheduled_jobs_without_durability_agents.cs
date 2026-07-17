using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Postgresql;
using Xunit.Abstractions;

namespace PostgresqlTests.MultiTenancy;

// GH-3376: scheduled job polling moved onto the distributed durability agent, and the node-wide
// fan-out stopped starting pollers of its own. Hosts running with DurabilityAgentEnabled = false
// have no NodeAgentController and therefore never build a durability agent, so for them the
// fan-out is still the only scheduled message pump and must keep working. Without this test, the
// #3376 fix could silently strand scheduled messages for mediator-style hosts.
public class scheduled_jobs_without_durability_agents : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private readonly TenantMessageTracker theTracker = new();
    private IHost theHost = null!;

    private string MainSchema => $"noagent_{theSuffix}";
    private string TenantDatabase => $"w3376_noagent_{theSuffix}";

    public scheduled_jobs_without_durability_agents(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        if (!await conn.DatabaseExists(TenantDatabase))
        {
            await new DatabaseSpecification().BuildDatabase(conn, TenantDatabase);
        }

        await conn.CloseAsync();

        var tenantConnectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = TenantDatabase
        }.ConnectionString;

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // The path under test: no durability agents, so no agent-hosted scheduled polling.
                opts.Durability.DurabilityAgentEnabled = false;

                opts.Durability.ScheduledJobPollingTime = 1.Seconds();
                opts.Durability.ScheduledJobFirstExecution = 500.Milliseconds();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, MainSchema)
                    .RegisterStaticTenants(tenants => tenants.Register("red", tenantConnectionString));

                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddSingleton(theTracker);

                opts.Policies.UseDurableLocalQueues();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<TenantScheduledMessageHandler>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task scheduled_tenant_message_still_runs_from_the_node_wide_fan_out()
    {
        var messageId = Guid.NewGuid();

        await theHost.MessageBus().ScheduleAsync(new TenantScheduledMessage(messageId), 1.Seconds(),
            new DeliveryOptions { TenantId = "red" });

        var handled = await Poll(30.Seconds(), () => theTracker.Received.Any(r => r.Id == messageId));

        handled.ShouldBeTrue(
            $"Scheduled message {messageId} was never handled - the node-wide scheduled job fan-out is " +
            "the only pump for hosts without durability agents");

        theTracker.Received.First(r => r.Id == messageId).TenantId.ShouldBe("red");
        _output.WriteLine($"Message {messageId} handled without any durability agent");
    }

    private static async Task<bool> Poll(TimeSpan timeout, Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return true;
            await Task.Delay(250.Milliseconds());
        }

        return condition();
    }
}
