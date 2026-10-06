using Bobcat;
using Bobcat.Xunit;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Bobcat.Tests;

// Under Bobcat.Xunit's [BobcatScenario] the same base class records into the scenario the adapter
// opens, so the test publishes as a specification with no Recordings helper in sight.
[BobcatFeature("Confirming appointments"), BobcatScenario]
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
