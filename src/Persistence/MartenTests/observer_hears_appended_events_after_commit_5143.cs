using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Marten;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace MartenTests;

/// <summary>
/// GH-5143: Marten always reported <c>IWolverineObserver.EventsAppended</c> after the commit. Polecat and
/// Fisher reported before it, so an observer heard about events whose commit then failed. The three stores
/// now share one after-commit report in core; this pins Marten to it.
/// </summary>
public class observer_hears_appended_events_after_commit_5143 : IAsyncLifetime
{
    private IHost theHost = null!;
    private WolverineRuntime theRuntime = null!;
    private IWolverineObserver theOriginalObserver = null!;
    private AppendedEventsObserver5143 theObserver = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Tracking.EnableEventAppendTracking = true;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "observer5143";
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(StartObservedStreamHandler5143));
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        theRuntime = theHost.GetRuntime();
        theOriginalObserver = theRuntime.Observer;
        theObserver = new AppendedEventsObserver5143(theRuntime);
        theRuntime.Observer = theObserver;
    }

    public async ValueTask DisposeAsync()
    {
        theRuntime.Observer = theOriginalObserver;
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task a_committed_append_is_reported()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeAsync(new StartObservedStream5143(id));

        theObserver.StreamIds.ShouldContain(id);
    }

    [Fact]
    public async Task an_append_whose_commit_fails_is_not_reported()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeAsync(new StartObservedStream5143(id));
        theObserver.StreamIds.Clear();

        // Starting the same stream again fails in the commit, after BeforeSaveChangesAsync has run
        await Should.ThrowAsync<Exception>(() => theHost.InvokeAsync(new StartObservedStream5143(id)));

        theObserver.StreamIds.ShouldNotContain(id);
    }
}

public record StartObservedStream5143(Guid Id);

public record ObservedStreamStarted5143(Guid Id);

public class ObservedStream5143
{
    public Guid Id { get; set; }
}

public static class StartObservedStreamHandler5143
{
    public static void Handle(StartObservedStream5143 command, IDocumentSession session)
    {
        session.Events.StartStream<ObservedStream5143>(command.Id, new ObservedStreamStarted5143(command.Id));
    }
}

// Re-implements only EventsAppended; everything else is the default persistence observer
internal class AppendedEventsObserver5143(IWolverineRuntime runtime)
    : PersistenceWolverineObserver(runtime), IWolverineObserver
{
    public List<Guid> StreamIds { get; } = new();

    public void EventsAppended(IReadOnlyList<IEvent> events)
    {
        lock (StreamIds) StreamIds.AddRange(events.Select(e => e.StreamId));
    }
}
