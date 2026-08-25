using IntegrationTests;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using MartenTests.AggregateHandlerWorkflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;

namespace MartenTests;

public record FindTimeline(Guid Id);

public record FindTimelineByAggregateId(Guid AggregateId);

public record Timeline(long Version, string[] EventTypes);

public static class FindTimelineHandler
{
    public static Timeline Handle(
        FindTimeline query,
        [StreamState] StreamState state,
        [StreamEvents] IReadOnlyList<IEvent> events)
    {
        return new Timeline(state.Version, events.Select(x => x.EventTypeName).ToArray());
    }

    public static Timeline Handle(
        FindTimelineByAggregateId query,
        [StreamState("AggregateId")] StreamState state,
        [StreamEvents("AggregateId")] IReadOnlyList<IEvent> events)
    {
        return new Timeline(state.Version, events.Select(x => x.EventTypeName).ToArray());
    }
}

/// <summary>
/// GH-3627. The message handler side of [StreamState] / [StreamEvents]. The HTTP side is covered by
/// Wolverine.Http.Tests, but the handler path had no coverage, and its identity resolution differs in
/// a way that is easy to get wrong: the parameter type is StreamState, not your aggregate, so the
/// "&lt;ParameterType&gt;Id" convention looks for "StreamStateId". Only a member named "Id" or the
/// explicit named-argument form resolves. A miss is an InvalidEntityLoadUsageException at bootstrap.
/// </summary>
public class stream_state_and_events_handlers_3627 : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FindTimelineHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> startStreamAsync()
    {
        var streamId = Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        session.Events.StartStream<LetterAggregate>(streamId, new AEvent(), new AEvent(), new CEvent());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return streamId;
    }

    [Fact]
    public async Task the_Id_convention_resolves_the_stream()
    {
        var streamId = await startStreamAsync();

        var timeline = await theHost.MessageBus()
            .InvokeAsync<Timeline>(new FindTimeline(streamId), TestContext.Current.CancellationToken);

        timeline.Version.ShouldBe(3);
        timeline.EventTypes.Length.ShouldBe(3);
    }

    [Fact]
    public async Task the_named_argument_form_resolves_a_differently_named_property()
    {
        var streamId = await startStreamAsync();

        var timeline = await theHost.MessageBus()
            .InvokeAsync<Timeline>(new FindTimelineByAggregateId(streamId), TestContext.Current.CancellationToken);

        timeline.Version.ShouldBe(3);
        timeline.EventTypes.Length.ShouldBe(3);
    }
}
