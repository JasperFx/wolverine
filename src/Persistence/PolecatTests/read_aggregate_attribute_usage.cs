using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Events;
using Polecat.Projections;
using PolecatTests.AggregateHandlerWorkflow;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;

namespace PolecatTests;

public class read_aggregate_attribute_usage : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FindLettersHandler));

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "read_agg";
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)theStore).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task use_end_to_end_happy_past()
    {
        var streamId = Guid.NewGuid();
        await using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream<LetterAggregate>(streamId, new AEvent(), new AEvent(), new CEvent());
            await session.SaveChangesAsync();

            var latest = await session.Events.FetchLatest<LetterAggregate>(streamId);
            latest.ShouldNotBeNull();
        }

        var envelope = await theHost.MessageBus().InvokeAsync<PcLetterAggregateEnvelope>(new PcFindAggregate(streamId));
        envelope.Inner.ACount.ShouldBe(2);
        envelope.Inner.CCount.ShouldBe(1);
    }

    [Fact]
    public async Task end_to_end_sad_path()
    {
        var envelope = await theHost.MessageBus()
            .InvokeAsync<PcLetterAggregateEnvelope>(new PcFindAggregate(Guid.NewGuid()));
        envelope.ShouldBeNull();
    }
}

public record PcLetterAggregateEnvelope(LetterAggregate Inner);

public record PcFindAggregate(Guid Id);

public static class FindLettersHandler
{
    public static PcLetterAggregateEnvelope Handle(PcFindAggregate command, [ReadAggregate] LetterAggregate aggregate)
    {
        return new PcLetterAggregateEnvelope(aggregate);
    }
}
