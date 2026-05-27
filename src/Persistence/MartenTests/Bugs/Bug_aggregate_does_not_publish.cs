using IntegrationTests;
using JasperFx.Resources;
using Marten;
using JasperFx.Events.Projections;
using MartenTests.AggregateHandlerWorkflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RDBMS;

namespace MartenTests.Bugs;

public class Bug_aggregate_does_not_publish : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        await dropSchema();
        
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.Policies.UseDurableLocalQueues();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AggregateHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Async);

                        m.DatabaseSchemaName = "nopublish";
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }
    
    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("nopublish");
        await conn.CloseAsync();
    }

    private async Task<long> PersistedIncomingCount()
    {
        await using var session = theStore.QuerySession();

        var command = session.Connection.CreateCommand(
            $"select count(*) from nopublish.{DatabaseConstants.IncomingTable}");

        var count = await command.ExecuteScalarAsync();
        return Convert.ToInt64(count);
    }

    [Fact]
    public async Task envelope_is_stored()
    {
        await theHost.MessageBus()
            .PublishAsync(new ScheduleSomething(Guid.NewGuid()));

        var count = await PersistedIncomingCount();
        count.ShouldBe(2); // ScheduleSomething + SomethingWasScheduled
    }

    [Fact]
    public async Task envelope_is_not_stored()
    {
        await theHost.MessageBus()
            .PublishAsync(new ScheduleSomethingUsingAggregate(Guid.NewGuid()));

        var count = await PersistedIncomingCount();
        count.ShouldBe(1); // ScheduleSomething, SomethingWasScheduled is missing
    }
}

public record ScheduleSomething(Guid Id);

public record ScheduleSomethingUsingAggregate(Guid Id);

public record SomethingWasScheduled(Guid Id);

public record AggregateCreated(Guid Id);

public sealed partial class Aggregate
{
    public static Aggregate Create(AggregateCreated @event)
    {
        return new Aggregate();
    }
}

public static class AggregateHandler
{
    public static SomethingWasScheduled Handle(ScheduleSomething command)
    {
        return new SomethingWasScheduled(command.Id);
    }
    
    public static SomethingWasScheduled Handle(ScheduleSomethingUsingAggregate command, [ReadAggregate(Required = false)] LetterAggregate aggregate)
    {
        return new SomethingWasScheduled(command.Id);
    }
    
    public static void Handle(SomethingWasScheduled message)
    {
    }
}