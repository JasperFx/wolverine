using Marten;
using JasperFx.CommandLine;
using Spectre.Console;
using TeleHealth.Common;

namespace TeleHealth.Backend;

[Description("Set up some providers and patients")]
public class SeedCommand : JasperFxAsyncCommand<NetCoreInput>
{
    public override async Task<bool> Execute(NetCoreInput input)
    {
        using var host = input.BuildHost();
        using var scope = host.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        await store.Advanced.Clean.DeleteAllDocumentsAsync();
        await store.Advanced.Clean.DeleteAllEventDataAsync();

        var patient1 = new Patient { FirstName = "Earvin", LastName = "Johnson" };
        var patient2 = new Patient { FirstName = "James", LastName = "Worthy" };
        var patient3 = new Patient { FirstName = "Byron", LastName = "Scott" };
        var patient4 = new Patient { FirstName = "Kareem", LastName = "Abdul Jabbar" };
        var patient5 = new Patient { FirstName = "Kurt", LastName = "Rambis" };

        await store.BulkInsertAsync(new[] { patient1, patient2, patient3, patient4, patient5 });

        var provider1 = new Provider { FirstName = "Larry", LastName = "Bird" };
        var provider2 = new Provider { FirstName = "Kevin", LastName = "McHale" };

        await store.BulkInsertAsync(new[] { provider1, provider2 });

        await using var session = store.LightweightSession();
        var boardId =
            session.Events.StartStream<Board>(new BoardOpened("Lakers", DateOnly.FromDateTime(DateTime.Today),
                DateTimeOffset.Now)).Id;

        var appt1 = session.Events.StartStream<Appointment>(new AppointmentRequested(patient1.Id)).Id;
        var appt2 = session.Events.StartStream<Appointment>(new AppointmentRequested(patient2.Id)).Id;
        var appt3 = session.Events.StartStream<Appointment>(new AppointmentRequested(patient3.Id)).Id;
        var appt4 = session.Events.StartStream<Appointment>(new AppointmentRequested(patient4.Id)).Id;
        var appt5 = session.Events.StartStream<Appointment>(new AppointmentRequested(patient5.Id)).Id;

        var shift1 = session.Events.StartStream<ProviderShift>(new ProviderJoined(provider1.Id, boardId)).Id;
        var shift2 = session.Events.StartStream<ProviderShift>(new ProviderJoined(provider2.Id, boardId)).Id;

        await session.SaveChangesAsync();

        session.Events.Append(appt1, new AppointmentRouted(boardId, patient1.Id, DateTimeOffset.Now.AddHours(1)));
        session.Events.Append(appt2, new AppointmentRouted(boardId, patient2.Id, DateTimeOffset.Now.AddHours(2)));
        session.Events.Append(appt3, new AppointmentRouted(boardId, patient3.Id, DateTimeOffset.Now.AddHours(3)));
        session.Events.Append(appt4, new AppointmentRouted(boardId, patient4.Id, DateTimeOffset.Now.AddHours(4)));
        session.Events.Append(appt5, new AppointmentRouted(boardId, patient5.Id, DateTimeOffset.Now.AddHours(5)));

        await session.SaveChangesAsync();

        session.Events.Append(appt1, new AppointmentScheduled(provider1.Id, DateTime.Now.AddMinutes(15)));
        session.Events.Append(appt2, new AppointmentScheduled(provider2.Id, DateTime.Now.AddMinutes(15)));

        session.Events.Append(shift1, new ProviderAssigned(appt1));
        session.Events.Append(shift2, new ProviderAssigned(appt2));

        await session.SaveChangesAsync();

        session.Events.Append(appt1, new AppointmentStarted(), new AppointmentFinished());
        session.Events.Append(appt2, new AppointmentStarted());

        session.Events.Append(shift1, new ChartingStarted(), new ChartingFinished());

        await session.SaveChangesAsync();

        AnsiConsole.Markup("[green]All data loaded successfully![/]");

        return true;
    }
}