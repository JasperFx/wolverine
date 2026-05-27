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

public abstract class Bug_aggregate_should_still_publish : PostgresqlContext, IAsyncLifetime
{
    protected abstract string Schema { get; }

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
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AggregateHandler))
                    .IncludeType(typeof(SomeOtherHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Async);

                        m.DatabaseSchemaName = Schema;
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

    private async Task dropSchema()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(Schema);
        await conn.CloseAsync();
    }

    private async Task<long> PersistedIncomingCount()
    {
        await using var session = theStore.QuerySession();

        var command = session.Connection.CreateCommand(
            $"select count(*) from {Schema}.{DatabaseConstants.IncomingTable}");

        var count = await command.ExecuteScalarAsync();
        return Convert.ToInt64(count);
    }

    public class Using_normal_handler : Bug_aggregate_should_still_publish
    {
        protected override string Schema => "publish_normal";
        
        [Fact]
        public async Task the_envelope_is_stored()
        {
            await theHost.MessageBus()
                .PublishAsync(new ScheduleSomething(Guid.NewGuid()));

            var count = await PersistedIncomingCount();
            count.ShouldBe(2); // ScheduleSomething + SomethingWasScheduled
        }
    }

    public class Using_aggregate_handler : Bug_aggregate_should_still_publish
    {
        protected override string Schema => "publish_aggregate";
        
        [Fact]
        public async Task envelope_is_not_stored()
        {
            await theHost.MessageBus()
                .PublishAsync(new ScheduleSomethingUsingAggregate(Guid.NewGuid()));

            var count = await PersistedIncomingCount();
            count.ShouldBe(1); // ScheduleSomething, SomethingWasScheduled is missing
            //count.ShouldBe(2);
        }
    }
}

public record ScheduleSomething(Guid Id);

public record ScheduleSomethingUsingAggregate(Guid Id);

public record SomethingWasScheduled(Guid Id);

public static class AggregateHandler
{
    public static SomethingWasScheduled Handle(
        ScheduleSomethingUsingAggregate command,
        [ReadAggregate(Required = false)] LetterAggregate aggregate)
    {
        return new SomethingWasScheduled(command.Id);
    }
}

public static class SomeOtherHandler
{
    public static SomethingWasScheduled Handle(ScheduleSomething command)
    {
        return new SomethingWasScheduled(command.Id);
    }

    public static void Handle(SomethingWasScheduled message)
    {
    }
}