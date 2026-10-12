using Bobcat;
using Bobcat.Xunit;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Bobcat.Tests;

// In a [BobcatFeature] class Bobcat.Xunit records every test, so the same base class records into the
// scenario the adapter opens and the test publishes as a specification with no Recordings helper in sight.
[BobcatFeature("Confirming appointments")]
[Collection(nameof(AppointmentsCollection))]
public class projected_lane(AppointmentsHost app) : WolverineSpec(app.Host)
{
    [Fact]
    public async Task a_scheduled_appointment_is_confirmed()
    {
        var id = Guid.NewGuid();

        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, new Patient("Ann", new Address("Austin")), []));
        await WhenReceived(new ConfirmAppointment(id));
        ThenEvents(typeof(AppointmentConfirmed));
        ThenMessageSent<SendConfirmationEmail>();

        var recording = ScenarioRecorder.Current.ShouldNotBeNull();
        recording.Steps.Select(x => x.Keyword).ShouldBe(new[] { "Given", "When", "Then", "And" });
    }
}

// Every act and message assertion works for an application with no event store at all
public class without_an_event_store : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(PatientArrivedHandler)))
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task messaging_only()
    {
        var scenario = new WolverineScenario(_host);

        await scenario.WhenPublished(new PatientArrived(Guid.NewGuid()));

        scenario.ThenMessageSent<PatientArrived>();
        scenario.ThenNoEvents();
        scenario.LastAct.Error.ShouldBeNull();
    }
}

// A spec built from the application's services alone, with no IHost handed over: the acts track the
// Wolverine runtime resolved from the container, and the store comes from it too
[BobcatFeature("Confirming appointments from the container")]
[Collection(nameof(AppointmentsCollection))]
public class projected_lane_over_services(AppointmentsHost app) : WolverineSpec(app.Host.Services)
{
    [Fact]
    public async Task a_scheduled_appointment_is_confirmed_with_no_host_in_hand()
    {
        var id = Guid.NewGuid();

        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, new Patient("Ann", new Address("Austin")), []));
        await WhenReceived(new ConfirmAppointment(id));
        ThenEvents(typeof(AppointmentConfirmed));
        ThenMessageSent<SendConfirmationEmail>();

        Should.Throw<InvalidOperationException>(() => Host);
    }

    [Fact]
    public void the_tracking_knobs_are_on_the_spec_itself()
    {
        Timeout = TimeSpan.FromSeconds(7);
        EventuallyTimeout = TimeSpan.FromSeconds(3);
        IncludeExternalTransports = false;

        Scenario.Timeout.ShouldBe(TimeSpan.FromSeconds(7));
        Scenario.EventuallyTimeout.ShouldBe(TimeSpan.FromSeconds(3));
        Scenario.IncludeExternalTransports.ShouldBeFalse();
    }
}
