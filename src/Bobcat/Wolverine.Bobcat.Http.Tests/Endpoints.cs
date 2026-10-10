using JasperFx.Events;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Bobcat.Http.Tests;

public record AppointmentScheduled(Guid AppointmentId, string Patient);

public record AppointmentConfirmed(Guid AppointmentId);

public record AppointmentCancelled(Guid AppointmentId, string Reason);

public record SendConfirmationEmail(Guid AppointmentId);

public class Appointment
{
    public Guid Id { get; set; }
    public string Patient { get; set; } = "";
    public bool Confirmed { get; set; }
    public bool Cancelled { get; set; }

    public void Apply(AppointmentScheduled e)
    {
        Id = e.AppointmentId;
        Patient = e.Patient;
    }

    public void Apply(AppointmentConfirmed e) => Confirmed = true;
    public void Apply(AppointmentCancelled e) => Cancelled = true;
}

public record ConfirmAppointmentRequest(Guid AppointmentId);

public record ConfirmationResponse(Guid AppointmentId, string Status);

public static class ConfirmAppointmentEndpoint
{
    public static ProblemDetails Validate(ConfirmAppointmentRequest request, Appointment appointment)
        => appointment.Confirmed
            ? new ProblemDetails { Detail = "The appointment is already confirmed", Status = 400 }
            : WolverineContinue.NoProblems;

    [WolverinePost("/api/appointments/confirm")]
    public static (ConfirmationResponse, EventsToAppend, OutgoingMessages) Post(ConfirmAppointmentRequest request,
        [WriteModel] Appointment appointment)
        => (new ConfirmationResponse(request.AppointmentId, "Confirmed"),
            new EventsToAppend { new AppointmentConfirmed(request.AppointmentId) },
            new OutgoingMessages { new SendConfirmationEmail(request.AppointmentId) });
}

public record CancelAppointment(Guid AppointmentId, string Reason);

public static class CancelAppointmentEndpoint
{
    // a route parameter AND a body: WhenPosted fills {appointmentId} from the command
    [WolverinePost("/api/appointments/{appointmentId}/cancel")]
    [EmptyResponse]
    public static AppointmentCancelled Post(CancelAppointment command, [WriteModel] Appointment appointment)
        => new(command.AppointmentId, command.Reason);
}

public record AmbiguousRequest(string Name);

public static class AmbiguousEndpoints
{
    [WolverinePost("/api/ambiguous/one")]
    public static string One(AmbiguousRequest request) => "one";

    [WolverinePost("/api/ambiguous/two")]
    public static string Two(AmbiguousRequest request) => "two";
}

public record ExplodingRequest(string Name);

public static class ExplodingEndpoint
{
    [WolverinePost("/api/explode")]
    public static string Post(ExplodingRequest request) => throw new DivideByZeroException("boom");
}

public static class SendConfirmationEmailHandler
{
    public static void Handle(SendConfirmationEmail message)
    {
    }
}

// GH-4931: an endpoint that appends and cascades, and one that starts a stream with an id it assigns
public record RemindPatient(Guid AppointmentId);

public record PatientReminded(Guid AppointmentId);

public record LogReminder(Guid AppointmentId);

public record ReminderLogged(Guid AppointmentId);

public static class RemindPatientEndpoint
{
    [WolverinePost("/api/appointments/remind")]
    public static (string, EventsToAppend, OutgoingMessages) Post(RemindPatient command, [WriteModel] Appointment appointment)
        => ("Reminded",
            new EventsToAppend { new PatientReminded(command.AppointmentId) },
            new OutgoingMessages { new LogReminder(command.AppointmentId) });
}

public static class LogReminderHandler
{
    public static void Handle(LogReminder message, [WriteModel] IEventStream<Appointment> appointment)
        => appointment.AppendOne(new ReminderLogged(message.AppointmentId));
}

public record BookAppointment(string Patient);

public static class BookAppointmentEndpoint
{
    [WolverinePost("/api/appointments/book")]
    [EmptyResponse]
    public static StartStream Post(BookAppointment command)
    {
        var id = Guid.CreateVersion7();
        return Storage.StartStream<Appointment>(id, new AppointmentScheduled(id, command.Patient));
    }
}
