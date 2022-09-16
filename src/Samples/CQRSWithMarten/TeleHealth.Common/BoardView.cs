using Baseline;
using Marten.Events;

namespace TeleHealth.Common;

public class BoardView
{
    public DateOnly Date { get; set; }

    public bool Active => Closed == null;

    public DateTimeOffset Opened { get; set; }
    public DateTimeOffset? Closed { get; set; }

    public DateTimeOffset? Finished { get; set; }

    public string CloseReason { get; set; }

    public string Name { get; set; }

    public Guid Id { get; set; }


    // Marten will helpfully help us out here
    // by setting the last Event sequence encountered
    public int Version { get; set; }

    public IList<BoardAppointment> Appointments { get; set; } = new List<BoardAppointment>();
    public IList<BoardProvider> Providers { get; set; } = new List<BoardProvider>();


    public BoardAppointment FindAppointment(Guid appointmentId)
    {
        return Appointments.Single(x => x.AppointmentId == appointmentId);
    }

    public BoardProvider FindProvider(Guid providerId)
    {
        return Providers.Single(x => x.Provider.Id == providerId);
    }

    public BoardProvider FindProviderByShift(IEvent @event)
    {
        return Providers.Single(x => x.ShiftId == @event.StreamId);
    }

    public void RemoveAppointment(Guid appointmentId)
    {
        Appointments.RemoveAll(x => x.AppointmentId == appointmentId);
    }

    public void RemoveProvider(Guid providerShiftId)
    {
        Providers.RemoveAll(x => x.ShiftId == providerShiftId);
    }
}

public class BoardAppointment
{
    public Guid AppointmentId { get; set; }
    public string PatientName { get; set; }
    public DateTimeOffset OriginalEstimatedTime { get; set; }
    public DateTimeOffset CurrentEstimatedTime { get; set; }
    public AppointmentStatus Status { get; set; }
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public Guid? ProviderId { get; set; }
}

public class BoardProvider
{
    public Provider Provider { get; set; }
    public Guid ShiftId { get; set; }
    public ProviderStatus Status { get; set; }
}
