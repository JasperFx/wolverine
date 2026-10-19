using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Bobcat.Tests;

public record Address(string City);

public record Patient(string Name, Address Address);

public record AppointmentScheduled(Guid AppointmentId, Patient Patient, List<string> Notes);

public record AppointmentConfirmed(Guid AppointmentId, DateTimeOffset ConfirmedAt);

public record ScheduleAppointment(Guid AppointmentId, Patient Patient, List<string> Notes);

public record ConfirmAppointment(Guid AppointmentId);

public record SendConfirmationEmail(Guid AppointmentId, string To);

public record PatientArrived(Guid AppointmentId);

public record CheckInPatient(Guid AppointmentId, string Desk, decimal Copay);

public record PatientCheckedIn(Guid AppointmentId, string Desk, DateTimeOffset CheckedInAt);

public record CopayCollected(Guid AppointmentId, decimal Amount);

public class Appointment
{
    public Guid Id { get; set; }
    public string Patient { get; set; } = "";
    public bool Confirmed { get; set; }

    public void Apply(AppointmentScheduled e)
    {
        Id = e.AppointmentId;
        Patient = e.Patient.Name;
    }

    public void Apply(AppointmentConfirmed e) => Confirmed = true;
}

public static class ScheduleAppointmentHandler
{
    public static StartStream Handle(ScheduleAppointment command)
        => Storage.StartStream<Appointment>(command.AppointmentId,
            new AppointmentScheduled(command.AppointmentId, command.Patient, command.Notes));
}

/// <summary>A typed refusal: what an event model's <c>x: Appointment already confirmed { appointment id }</c> becomes.</summary>
public class AppointmentAlreadyConfirmed(Guid appointmentId)
    : InvalidOperationException("The appointment is already confirmed")
{
    public Guid AppointmentId { get; } = appointmentId;
}

public static class ConfirmAppointmentHandler
{
    public static (AppointmentConfirmed, OutgoingMessages) Handle(ConfirmAppointment command,
        [WriteModel] Appointment appointment)
    {
        if (appointment.Confirmed) throw new AppointmentAlreadyConfirmed(command.AppointmentId);

        return (new AppointmentConfirmed(command.AppointmentId, DateTimeOffset.UtcNow),
            new OutgoingMessages { new SendConfirmationEmail(command.AppointmentId, appointment.Patient) });
    }
}

// Two events from one command, so the any-order and contains assertions have something to choose between
public static class CheckInPatientHandler
{
    public static Events Handle(CheckInPatient command, [WriteModel] Appointment appointment)
        => [new PatientCheckedIn(command.AppointmentId, command.Desk, DateTimeOffset.UtcNow),
            new CopayCollected(command.AppointmentId, command.Copay)];
}

public static class SendConfirmationEmailHandler
{
    public static void Handle(SendConfirmationEmail message)
    {
    }
}

public static class PatientArrivedHandler
{
    public static void Handle(PatientArrived message)
    {
    }
}

// GH-5077: a timeout whose handler escalates, and a command whose handler schedules it
public record ConfirmationOverdue(Guid AppointmentId) : TimeoutMessage(TimeSpan.FromDays(5));

public record EscalateUnconfirmedAppointment(Guid AppointmentId);

public record AwaitConfirmation(Guid AppointmentId);

public static class AwaitConfirmationHandler
{
    public static ConfirmationOverdue Handle(AwaitConfirmation command) => new(command.AppointmentId);
}

public static class ConfirmationOverdueHandler
{
    public static EscalateUnconfirmedAppointment Handle(ConfirmationOverdue message) => new(message.AppointmentId);
}

public static class EscalateUnconfirmedAppointmentHandler
{
    public static void Handle(EscalateUnconfirmedAppointment message)
    {
    }
}

// wolverine#5077: a timeout whose handler appends to the appointment's stream
public record CheckInWindowClosed(Guid AppointmentId) : TimeoutMessage(TimeSpan.FromHours(1));

public record PatientNoShow(Guid AppointmentId);

public static class CheckInWindowClosedHandler
{
    public static Events Handle(CheckInWindowClosed message, [WriteModel] Appointment appointment)
        => [new PatientNoShow(message.AppointmentId)];
}
