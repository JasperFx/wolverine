using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests;

public class read_aggregate_attribute_usage : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost;
    private IDocumentStore theStore;
    private Guid theStreamId;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FindLettersHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Async);

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

    [Fact]
    public async Task use_end_to_end_happy_past()
    {
        var streamId = Guid.NewGuid();
        using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream<LetterAggregate>(streamId, new AEvent(), new AEvent(), new CEvent());
            await session.SaveChangesAsync();

            var latest = await session.Events.FetchLatest<LetterAggregate>(streamId);
            latest.ShouldNotBeNull();
        }

        var envelope = await theHost.MessageBus().InvokeAsync<LetterAggregateEnvelope>(new FindAggregate(streamId));
        envelope.Inner.ACount.ShouldBe(2);
        envelope.Inner.CCount.ShouldBe(1);
    }

    [Fact]
    public async Task end_to_end_sad_path()
    {
        var envelope = await theHost.MessageBus()
            .InvokeAsync<LetterAggregateEnvelope>(new FindAggregate(Guid.NewGuid()));
        envelope.ShouldBeNull();
    }
}

public record LetterAggregateEnvelope(LetterAggregate Inner);

#region sample_using_ReadAggregate_in_messsage_handlers

public record FindAggregate(Guid Id);

public static class FindLettersHandler
{
    // This is admittedly just some weak sauce testing support code
    public static LetterAggregateEnvelope Handle(FindAggregate command, [ReadAggregate] LetterAggregate aggregate)
    {
        return new LetterAggregateEnvelope(aggregate);
    }
}

#endregion