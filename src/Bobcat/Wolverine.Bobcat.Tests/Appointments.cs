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

public static class ConfirmAppointmentHandler
{
    public static (AppointmentConfirmed, OutgoingMessages) Handle(ConfirmAppointment command,
        [WriteModel] Appointment appointment)
    {
        if (appointment.Confirmed) throw new InvalidOperationException("The appointment is already confirmed");

        return (new AppointmentConfirmed(command.AppointmentId, DateTimeOffset.UtcNow),
            new OutgoingMessages { new SendConfirmationEmail(command.AppointmentId, appointment.Patient) });
    }
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
