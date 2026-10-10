using IntegrationTests;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace MartenTests.Tracking;

// GH-4931: a tracked session hears what every event-store session committed during it, attributed to the
// message being handled, so a test never has to work out from the store which events were its own
public class tracked_sessions_record_appended_events_4931 : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TripHandlers4931));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services
                    .AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "tracked_appends";
                        m.Events.RegisterTagType<DriverId4931>();
                        m.Events.TagWith<TripStarted4931>(e => e.Driver);
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_started_stream_is_recorded_with_the_id_the_handler_assigned()
    {
        var command = new StartTrip4931(new DriverId4931(Guid.NewGuid()));
        var session = await _host.InvokeMessageAndWaitAsync(command);

        var appended = session.AppendedEvents.ShouldHaveSingleItem();
        appended.Envelope.ShouldNotBeNull().Message.ShouldBeSameAs(command);

        var started = appended.StartedStreams.ShouldHaveSingleItem();
        started.Id.ShouldNotBe(Guid.Empty);
        started.Events.Single().Data.ShouldBeOfType<TripStarted4931>();

        // The stream really is the one the store holds
        await using var query = _host.DocumentStore().QuerySession();
        var events = await query.Events.FetchStreamAsync(started.Id, token: TestContext.Current.CancellationToken);
        events.Single().Data.ShouldBeOfType<TripStarted4931>();
    }

    [Fact]
    public async Task each_session_is_attributed_to_the_message_it_handled_cascades_included()
    {
        var start = await _host.InvokeMessageAndWaitAsync(new StartTrip4931(new DriverId4931(Guid.NewGuid())));
        var tripId = start.AppendedEvents.Single().StartedStreams.Single().Id;

        var command = new CompleteTrip4931(tripId);
        var session = await _host.InvokeMessageAndWaitAsync(command);

        session.AppendedEvents.Count.ShouldBe(2);

        // Only what the original message's own session appended
        var own = session.AppendedEvents.Single(x => ReferenceEquals(x.Envelope?.Message, command));
        own.Events.Select(x => x.Data.GetType()).ShouldBe([typeof(TripCompleted4931)]);
        own.StartedStreams.ShouldBeEmpty();
        own.Streams.Single().Id.ShouldBe(tripId);

        // ... and the cascade's, separately
        var cascade = session.AppendedEvents.Single(x => x.Envelope?.Message is NotifyDriver4931);
        cascade.Events.Select(x => x.Data.GetType()).ShouldBe([typeof(DriverNotified4931)]);

        // Versions are the committed ones
        own.Events.Single().Version.ShouldBe(2);
        cascade.Events.Single().Version.ShouldBe(3);
    }

    [Fact]
    public async Task the_events_keep_their_tags_which_no_store_reads_back()
    {
        var driver = new DriverId4931(Guid.NewGuid());
        var session = await _host.InvokeMessageAndWaitAsync(new StartTrip4931(driver));

        var @event = session.AppendedEvents.Single().Events.Single();
        @event.Tags.ShouldNotBeNull().ShouldContain(new EventTag(typeof(DriverId4931), driver));
    }
}

public record struct DriverId4931(Guid Value);

public record StartTrip4931(DriverId4931 Driver);

public record CompleteTrip4931(Guid TripId);

public record NotifyDriver4931(Guid TripId);

public record TripStarted4931(DriverId4931 Driver);

public record TripCompleted4931;

public record DriverNotified4931;

public class Trip4931
{
    public Guid Id { get; set; }

    public static Trip4931 Create(TripStarted4931 e) => new();

    public void Apply(TripCompleted4931 e)
    {
    }

    public void Apply(DriverNotified4931 e)
    {
    }
}

[WolverineIgnore]
public static class TripHandlers4931
{
    public static StartStream Handle(StartTrip4931 command)
        => Storage.StartStream<Trip4931>(Guid.CreateVersion7(), new TripStarted4931(command.Driver));

    public static NotifyDriver4931 Handle(
        CompleteTrip4931 command,
        [WriteModel(nameof(CompleteTrip4931.TripId))] IEventStream<Trip4931> stream)
    {
        stream.AppendOne(new TripCompleted4931());
        return new NotifyDriver4931(command.TripId);
    }

    public static void Handle(
        NotifyDriver4931 command,
        [WriteModel(nameof(NotifyDriver4931.TripId))] IEventStream<Trip4931> stream)
        => stream.AppendOne(new DriverNotified4931());
}
