using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Weasel.Postgresql.Tables;

namespace TeleHealth.Common;

public class Patient
{
    public Guid Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
}

public record AppointmentRequested(Guid PatientId);

public record AppointmentRouted(Guid BoardId, Guid PatientId, DateTimeOffset EstimatedTime);

public record AppointmentScheduled(
    Guid ProviderId,
    DateTimeOffset EstimatedTime
);

public record AppointmentStarted;

public record AppointmentFinished;

public class AppointmentDurationProjection : EventProjection
{
    public AppointmentDurationProjection()
    {
        // Defining an extra table so Marten
        // can manage it for us behind the scenes
        var table = new Table("appointment_duration");
        table.AddColumn<Guid>("id").AsPrimaryKey();
        table.AddColumn<DateTimeOffset>("start");
        table.AddColumn<DateTimeOffset>("end");

        SchemaObjects.Add(table);

        // This is to let Marten know that we want the data
        // in this table wiped out before doing a rebuild
        // of this projection
        Options.DeleteDataInTableOnTeardown(table.Identifier);
    }

    public void Apply(
        IEvent<AppointmentStarted> @event,
        IDocumentOperations ops)
    {
        var sql = "insert into appointment_duration "
                  + "(id, start) values (?, ?)";
        ops.QueueSqlCommand(sql,
            @event.Id,
            @event.Timestamp);
    }

    public void Apply(
        IEvent<AppointmentFinished> @event,
        IDocumentOperations ops)
    {
        var sql = "update appointment_duration "
                  + "set end = ? where id = ?";
        ops.QueueSqlCommand(sql,
            @event.Timestamp,
            @event.Id);
    }
}

public enum AppointmentStatus
{
    Requested,
    Scheduled,
    Started,
    Completed
}

public class Appointment
{
    public Appointment(string firstName, string lastName)
    {
        FirstName = firstName;
        LastName = lastName;
    }

    public Guid Id { get; set; }

    public int Version { get; set; }
    public string FirstName { get; }
    public string LastName { get; }

    public AppointmentStatus Status { get; set; }
    public string ProviderName { get; set; }
    public DateTimeOffset? EstimatedTime { get; set; }
    public Guid BoardId { get; set; }
}