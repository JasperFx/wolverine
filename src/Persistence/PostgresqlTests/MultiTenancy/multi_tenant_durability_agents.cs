using System.Collections.Concurrent;
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
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Xunit.Abstractions;

namespace PostgresqlTests.MultiTenancy;

public class multi_tenant_durability_agents : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost theHost;
    private string tenant1ConnectionString;
    private string tenant2ConnectionString;
    private TenantMessageTracker theTracker = new();

    public multi_tenant_durability_agents(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "db1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "db2");

        await conn.CloseAsync();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();
                opts.Durability.ScheduledJobFirstExecution = 500.Milliseconds();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "mt_durability")
                    .RegisterStaticTenants(tenants =>
                    {
                        tenants.Register("red", tenant1ConnectionString);
                        tenants.Register("blue", tenant2ConnectionString);
                    });

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
    public async Task all_tenant_durability_agents_are_started()
    {
        // Should have 3 durability agents: main + red + blue
        var result = await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = PersistenceConstants.AgentScheme;
            w.ExpectRunningAgents(theHost, 3);
        }, 30.Seconds());

        result.ShouldBeTrue("Expected 3 durability agents (main + 2 tenants) to be running");

        var agents = theHost.RunningAgents()
            .Where(u => u.Scheme == PersistenceConstants.AgentScheme)
            .ToArray();

        _output.WriteLine("Running durability agents:");
        foreach (var agent in agents)
        {
            _output.WriteLine($"  {agent}");
        }

        agents.Length.ShouldBe(3);
    }

    [Fact]
    public async Task scheduled_message_for_tenant_is_processed()
    {
        // Wait for durability agents to start
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = PersistenceConstants.AgentScheme;
            w.ExpectRunningAgents(theHost, 3);
        }, 30.Seconds());

        var messageId = Guid.NewGuid();

        // Schedule a message for the "red" tenant with a short delay
        var bus = theHost.MessageBus();
        await bus.ScheduleAsync(new TenantScheduledMessage(messageId), 2.Seconds(),
            new DeliveryOptions { TenantId = "red" });

        _output.WriteLine($"Scheduled message {messageId} for tenant 'red'");

        // Poll until handled
        var handled = await Poll(30.Seconds(), () =>
            theTracker.Received.Any(r => r.Id == messageId));

        handled.ShouldBeTrue($"Scheduled message {messageId} for tenant 'red' was not handled within timeout");

        var received = theTracker.Received.First(r => r.Id == messageId);
        received.TenantId.ShouldBe("red");

        _output.WriteLine($"Message {messageId} handled with tenant '{received.TenantId}'");
    }

    [Fact]
    public async Task scheduled_messages_for_all_tenants_are_processed()
    {
        // Wait for durability agents to start
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = PersistenceConstants.AgentScheme;
            w.ExpectRunningAgents(theHost, 3);
        }, 30.Seconds());

        var redId = Guid.NewGuid();
        var blueId = Guid.NewGuid();
        var mainId = Guid.NewGuid();

        var bus = theHost.MessageBus();

        // Schedule messages for each tenant and the main database
        await bus.ScheduleAsync(new TenantScheduledMessage(redId), 2.Seconds(),
            new DeliveryOptions { TenantId = "red" });
        await bus.ScheduleAsync(new TenantScheduledMessage(blueId), 2.Seconds(),
            new DeliveryOptions { TenantId = "blue" });
        await bus.ScheduleAsync(new TenantScheduledMessage(mainId), 2.Seconds());

        _output.WriteLine($"Scheduled 3 messages: red={redId}, blue={blueId}, main={mainId}");

        // Wait for all 3 to arrive
        var allHandled = await Poll(30.Seconds(), () =>
            theTracker.Received.Any(r => r.Id == redId) &&
            theTracker.Received.Any(r => r.Id == blueId) &&
            theTracker.Received.Any(r => r.Id == mainId));

        allHandled.ShouldBeTrue("Not all scheduled messages were handled within timeout");

        theTracker.Received.First(r => r.Id == redId).TenantId.ShouldBe("red");
        theTracker.Received.First(r => r.Id == blueId).TenantId.ShouldBe("blue");

        // Main database message should have null or empty tenant id
        var mainReceived = theTracker.Received.First(r => r.Id == mainId);
        _output.WriteLine($"Main message tenant: '{mainReceived.TenantId}'");
    }

    private static async Task<bool> Poll(TimeSpan timeout, Func<bool> condition)
    {
        var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return true;
            await Task.Delay(250.Milliseconds());
        }

        return condition();
    }

    private async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }

        builder.Database = databaseName;
        return builder.ConnectionString;
    }
}

public record TenantScheduledMessage(Guid Id);

public class TenantMessageTracker
{
    public ConcurrentBag<(Guid Id, string? TenantId)> Received { get; } = new();
}

public class TenantScheduledMessageHandler
{
    public static void Handle(TenantScheduledMessage message, Envelope envelope, TenantMessageTracker tracker)
    {
        tracker.Received.Add((message.Id, envelope.TenantId));
    }
}
