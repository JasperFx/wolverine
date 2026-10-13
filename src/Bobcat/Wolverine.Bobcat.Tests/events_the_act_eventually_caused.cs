using Bobcat.Engine;
using Fisher;
using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Wolverine.Bobcat.Tests.TheActsOwnEvents;
using Wolverine.Fisher;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat;
using PolecatStore = global::Polecat.IDocumentStore;

namespace Wolverine.Bobcat.Tests.EventsTheActEventuallyCaused;

// GH-4939: ThenEventuallyCaused finds the events an act caused past an asynchronous boundary the tracked
// session cannot see -- here a message an event subscription publishes -- by querying the store for the act's
// correlation id once the async daemons have caught up. On Marten and Polecat, which relay events to Wolverine
// through a subscription; Fisher has no event subscriptions, so it covers the store query and the refusals.

public record ShipParcel(Guid ParcelId);
public record PrintLabel(Guid ParcelId);
public record NotifyRecipient(Guid ParcelId);

public record ParcelBooked(string Recipient);
public record ParcelShipped;
public record LabelPrinted(Guid ParcelId);
public record RecipientNotified(Guid ParcelId);

public class Parcel
{
    public Guid Id { get; set; }
    public static Parcel Create(ParcelBooked e) => new();
    public void Apply(ParcelShipped e) { }
}

public class ShippingLabel
{
    public Guid Id { get; set; }
    public static ShippingLabel Create(LabelPrinted e) => new();
}

public class Notification
{
    public Guid Id { get; set; }
    public static Notification Create(RecipientNotified e) => new();
}

public static class ParcelHandlers
{
    // Ships the parcel, and cascades PrintLabel: a message the tracked session sees
    public static PrintLabel Handle(ShipParcel command, [WriteModel(nameof(ShipParcel.ParcelId))] IEventStream<Parcel> parcel)
    {
        parcel.AppendOne(new ParcelShipped());
        return new PrintLabel(command.ParcelId);
    }

    public static StartStream Handle(PrintLabel command)
        => Storage.StartStream<ShippingLabel>(Guid.CreateVersion7(), new LabelPrinted(command.ParcelId));

    // Only ever published by the event subscription, past the asynchronous boundary
    public static StartStream Handle(NotifyRecipient command)
        => Storage.StartStream<Notification>(Guid.CreateVersion7(), new RecipientNotified(command.ParcelId));
}

#region sample_bobcat_pass_the_correlation_id_on

public static class ParcelSubscription
{
    // The subscription passes the event's correlation id on to the message it publishes, so the events that
    // message's handler appends are recorded with the act's correlation id too
    public static ValueTask Relay(IEvent<ParcelShipped> e, IMessageBus bus)
        => bus.PublishAsync(new NotifyRecipient(e.StreamId), new DeliveryOptions { CorrelationId = e.CorrelationId });
}

#endregion

public abstract class ParcelHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    /// <summary>
    /// Why this store cannot carry the act's correlation id past an event subscription, or null when it can.
    /// </summary>
    public abstract string? WhyNoAsyncBoundary { get; }

    protected abstract void ConfigureStore(WolverineOptions opts);

    protected virtual Task AfterStartAsync() => Task.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ParcelHandlers));
                ConfigureStore(opts);
            })
            .StartAsync();

        await AfterStartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is null) return;

        await Host.StopAsync();
        Host.Dispose();
    }
}

#region sample_bobcat_events_past_an_async_boundary_marten

public sealed class MartenParcelHost : ParcelHost
{
    public override string? WhyNoAsyncBoundary => null;

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "bobcat_eventually_caused";
                m.DisableNpgsqlLogging = true;

                // ThenEventuallyCaused queries the store by the act's correlation id, which Marten only
                // records when this is on
                m.Events.MetadataConfig.CorrelationIdEnabled = true;
            })
            .IntegrateWithWolverine()
            .AddAsyncDaemon(DaemonMode.Solo)
            .PublishEventsToWolverine("parcels", relay => relay.PublishEvent<ParcelShipped>(ParcelSubscription.Relay));
}

#endregion

public sealed class PolecatParcelHost : ParcelHost
{
    // The relay is configured all the same, so the test runs the day the loader reads the metadata columns
    public override string? WhyNoAsyncBoundary =>
        "Polecat's async daemon loads events without their correlation id (PolecatEventLoader selects no metadata " +
        "columns), so an event subscription has none to pass on";

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddPolecat(m =>
            {
                m.ConnectionString = Servers.SqlServerConnectionString;
                m.DatabaseSchemaName = "bobcat_eventually_caused";
                m.Events.EnableCorrelationId = true;
            })
            .IntegrateWithWolverine()
            .AddAsyncDaemon(DaemonMode.Solo)
            .PublishEventsToWolverine("parcels", relay => relay.PublishEvent<ParcelShipped>(ParcelSubscription.Relay));

    protected override Task AfterStartAsync()
        => ((global::Polecat.DocumentStore)Host.Services.GetRequiredService<PolecatStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();
}

public sealed class FisherParcelHost : ParcelHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat_eventually_caused_{Guid.NewGuid():N}.db");

    public override string? WhyNoAsyncBoundary => "Fisher has no event subscriptions";

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddFisher(o =>
            {
                o.Connection($"Data Source={_file}");
                o.AutoCreateSchemaObjects = AutoCreate.All;
                o.Events.EnableCorrelationId = true;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
}

public abstract class events_the_act_eventually_caused<THost>(THost app) : WolverineSpec(app.Host)
    where THost : ParcelHost
{
    private async Task<Guid> aBookedParcel()
    {
        var theParcel = Guid.CreateVersion7();
        await GivenEvents<Parcel>(theParcel, new ParcelBooked("Ann"));
        return theParcel;
    }

    [Fact]
    public async Task what_the_act_caused_in_the_tracked_session_is_found_by_its_correlation_id()
    {
        var theParcel = await aBookedParcel();

        await WhenReceived(new ShipParcel(theParcel));

        LastAct.CorrelationId.ShouldNotBeNull();
        LastAct.CorrelationId.ShouldBe(ActCorrelationId);

        // The act's own event and its cascade's, both recorded with the act's correlation id
        await ThenEventuallyCaused(new ParcelShipped(), new LabelPrinted(theParcel));
        await ThenEventuallyCaused<LabelPrinted>();
    }

    [Fact]
    public async Task every_act_runs_under_a_correlation_id_of_its_own()
    {
        var theParcel = await aBookedParcel();

        await WhenReceived(new ShipParcel(theParcel));
        var first = LastAct.CorrelationId;

        await WhenReceived(new ShipParcel(theParcel));
        LastAct.CorrelationId.ShouldNotBe(first);
    }

    [Fact]
    public async Task the_step_reads_as_eventually_emitted()
    {
        var theParcel = await aBookedParcel();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ShipParcel(theParcel));
            await ThenEventuallyCaused(new LabelPrinted(theParcel));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps[^1].Text.ShouldBe("LabelPrinted is eventually emitted");
    }

    [Fact]
    public async Task an_event_the_act_never_caused_fails_once_the_wait_is_over()
    {
        Scenario.EventuallyTimeout = TimeSpan.FromMilliseconds(500);
        var theParcel = await aBookedParcel();

        await WhenReceived(new ShipParcel(theParcel));

        var failure = await Should.ThrowAsync<Exception>(() => ThenEventuallyCaused(new LabelPrinted(Guid.NewGuid())));
        failure.Message.ShouldContain(LastAct.CorrelationId!);
    }

    [Fact]
    public async Task an_event_appended_past_an_event_subscription_is_found_once_the_daemons_catch_up()
    {
        Assert.SkipWhen(app.WhyNoAsyncBoundary is not null, app.WhyNoAsyncBoundary ?? "");

        #region sample_bobcat_then_eventually_caused

        var theParcel = await aBookedParcel();

        await WhenReceived(new ShipParcel(theParcel));

        // RecipientNotified is appended by the handler of a message an event subscription published, after the
        // tracked session is over: found in the store by the act's correlation id
        await ThenEventuallyCaused(new RecipientNotified(theParcel));

        #endregion
    }
}

public sealed class marten_events_the_act_eventually_caused(MartenParcelHost app)
    : events_the_act_eventually_caused<MartenParcelHost>(app), IClassFixture<MartenParcelHost>;

public sealed class polecat_events_the_act_eventually_caused(PolecatParcelHost app)
    : events_the_act_eventually_caused<PolecatParcelHost>(app), IClassFixture<PolecatParcelHost>;

public sealed class fisher_events_the_act_eventually_caused(FisherParcelHost app)
    : events_the_act_eventually_caused<FisherParcelHost>(app), IClassFixture<FisherParcelHost>;

// GH-4939: correlation ids are off by default on every store, and a skipped "then" looks green, so the step
// fails and says how to turn them on for the store it is running against. The Tab hosts leave them off.
public abstract class correlation_ids_off<THost>(THost app, string howToTurnThemOn) : WolverineSpec(app.Host)
    where THost : TabHost
{
    [Fact]
    public async Task the_step_fails_and_says_how_to_turn_them_on()
    {
        await WhenReceived(new OpenTab("the regulars"));

        var failure = await Should.ThrowAsync<Exception>(() => ThenEventuallyCaused<TabOpened>());
        failure.Message.ShouldContain("does not record correlation ids");
        failure.Message.ShouldContain(howToTurnThemOn);
    }
}

public sealed class marten_correlation_ids_off(MartenTabHost app)
    : correlation_ids_off<MartenTabHost>(app, "opts.Events.MetadataConfig.CorrelationIdEnabled = true"), IClassFixture<MartenTabHost>;

public sealed class polecat_correlation_ids_off(PolecatTabHost app)
    : correlation_ids_off<PolecatTabHost>(app, "AddPolecat(): opts.Events.EnableCorrelationId = true"), IClassFixture<PolecatTabHost>;

public sealed class fisher_correlation_ids_off(FisherTabHost app)
    : correlation_ids_off<FisherTabHost>(app, "AddFisher(): opts.Events.EnableCorrelationId = true"), IClassFixture<FisherTabHost>;
