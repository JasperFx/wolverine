using IntegrationTests;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using Marten.Events.Projections;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

public class override_of_event_metadata
{
    [Fact]
    public async Task return_event_with_metadata_from_aggregate_handler()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AEventHandler))
                    .IncludeType(typeof(EmitEventsWithMetadataHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);

                        m.DisableNpgsqlLogging = true;
                        m.Events.MetadataConfig.HeadersEnabled = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine(x =>
                    {
                        x.UseFastEventForwarding = true;
                    });

                opts.Policies.AutoApplyTransactions();


                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var id = Guid.NewGuid();
        
        using var session = host.DocumentStore().LightweightSession();
        session.Events.StartStream<LetterAggregate>(id, new AEvent());
        await session.SaveChangesAsync();

        await host.InvokeMessageAndWaitAsync(new EmitEventsWithMetadata(id));

        var stream = await session.Events.FetchStreamAsync(id);

        foreach (var e in stream)
        {
            e.DotNetTypeName.ShouldNotBeNull();
            e.EventTypeName.ShouldNotBeNull();
        }
        
        stream.OfType<Event<CEvent>>().Single().Headers["name"].ShouldBe("perrin");
    }
}

public record EmitEventsWithMetadata(Guid Id);

public static class EmitEventsWithMetadataHandler
{
    [AggregateHandler]
    public static IEnumerable<object> Handle(EmitEventsWithMetadata command, LetterAggregate aggregate)
    {
        yield return new BEvent();
        var c = Event.For(new CEvent());
        c.SetHeader("name", "perrin");
        
        yield return c;

        yield return new DEvent();
    }
}

