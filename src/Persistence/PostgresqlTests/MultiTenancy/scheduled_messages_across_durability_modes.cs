using IntegrationTests;
using JasperFx;
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

// GH-3376: scheduled job polling for TENANT databases moved onto the distributed durability agent, while
// main / ancillary stores kept polling from the node-wide fan-out. That split has to hold in every
// durability mode, for both the main store and a tenant database.
//
// These deliberately run with the DEFAULT durability timings. An earlier cut of the #3376 fix moved main
// store polling onto the agent too, which meant polling no longer started until leader election and agent
// assignment finished - scheduled messages went from prompt to ~13s late on every startup. Every test
// that tightened HealthCheckPollingTime / CheckAssignmentPeriod / ScheduledJobPollingTime sailed straight
// past it; only a default-timings test caught it. Do not "speed these up".
public class scheduled_messages_across_durability_modes : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly List<IHost> _hosts = [];
    private readonly TenantMessageTracker theTracker = new();
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];

    private string MainSchema => $"modes_{theSuffix}";
    private string TenantDatabase => $"w3376_modes_{theSuffix}";

    public scheduled_messages_across_durability_modes(ITestOutputHelper output)
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
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private async Task<IHost> StartHostAsync(DurabilityMode mode)
    {
        var tenantConnectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = TenantDatabase
        }.ConnectionString;

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;

                // Default durability timings on purpose - see the class comment.

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, MainSchema)
                    .RegisterStaticTenants(tenants => tenants.Register("red", tenantConnectionString));

                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddSingleton(theTracker);

                opts.Policies.UseDurableLocalQueues();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<TenantScheduledMessageHandler>();
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    [Theory]
    [InlineData(DurabilityMode.Solo)]
    [InlineData(DurabilityMode.Balanced)]
    public async Task scheduled_message_against_the_main_store_executes(DurabilityMode mode)
    {
        var host = await StartHostAsync(mode);

        var messageId = Guid.NewGuid();
        await host.MessageBus().ScheduleAsync(new TenantScheduledMessage(messageId), 1.Seconds());

        // The main store polls from the node-wide fan-out, which starts at boot, so this must not have
        // to wait on leader election or agent assignment.
        var elapsed = await TimeUntilHandledAsync(messageId, 20.Seconds());

        elapsed.ShouldNotBeNull(
            $"Scheduled main-store message was never executed in {mode} mode. The main store polls from " +
            "the node-wide fan-out - if that stopped, or now waits on agent assignment, this is where it shows.");

        _output.WriteLine($"{mode}/main: executed after {elapsed.Value.TotalSeconds:N1}s");
    }

    [Theory]
    [InlineData(DurabilityMode.Solo)]
    [InlineData(DurabilityMode.Balanced)]
    public async Task scheduled_message_against_a_tenant_database_executes(DurabilityMode mode)
    {
        var host = await StartHostAsync(mode);

        var messageId = Guid.NewGuid();
        await host.MessageBus().ScheduleAsync(new TenantScheduledMessage(messageId), 1.Seconds(),
            new DeliveryOptions { TenantId = "red" });

        // A tenant database is polled by its durability agent, so in Balanced mode this legitimately
        // waits for leader election and assignment first - hence the roomier budget.
        var elapsed = await TimeUntilHandledAsync(messageId, 60.Seconds());

        elapsed.ShouldNotBeNull(
            $"Scheduled tenant message was never executed in {mode} mode. Tenant polling rides the " +
            "distributed durability agent - if it never started, this is where it shows.");

        theTracker.Received.First(r => r.Id == messageId).TenantId.ShouldBe("red");
        _output.WriteLine($"{mode}/tenant: executed after {elapsed.Value.TotalSeconds:N1}s");
    }

    private async Task<TimeSpan?> TimeUntilHandledAsync(Guid messageId, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.IsCancellationRequested)
        {
            if (theTracker.Received.Any(r => r.Id == messageId)) return stopwatch.Elapsed;
            await Task.Delay(100);
        }

        return theTracker.Received.Any(r => r.Id == messageId) ? stopwatch.Elapsed : null;
    }
}
