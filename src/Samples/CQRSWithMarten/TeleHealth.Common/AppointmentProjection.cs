using Marten;
using Marten.Events.Aggregation;

namespace TeleHealth.Common;

public class AppointmentProjection : SingleStreamProjection<Appointment>
{
    public AppointmentProjection()
    {
        DeleteEvent<ChartingFinished>();
    }

    public async Task<Appointment> Create(AppointmentRequested requested, IQuerySession session)
    {
        var patient = await session.LoadAsync<Patient>(requested.PatientId);

        return new Appointment(patient.FirstName, patient.LastName)
        {
            Status = AppointmentStatus.Requested
        };
    }

    public void Apply(AppointmentRouted routed, Appointment appointment)
    {
        appointment.BoardId = routed.BoardId;
        appointment.EstimatedTime = routed.EstimatedTime;
    }

    public async Task Apply(AppointmentScheduled scheduled, Appointment appointment, IQuerySession session)
    {
        var provider = await session.LoadAsync<Provider>(scheduled.ProviderId);
        appointment.ProviderName = $"{provider.FirstName} {provider.LastName}";
        appointment.Status = AppointmentStatus.Scheduled;
        appointment.EstimatedTime = scheduled.EstimatedTime;
    }

    public void Apply(AppointmentStarted started, Appointment appointment)
    {
        appointment.Status = AppointmentStatus.Started;
    }

    public void Apply(AppointmentFinished finished, Appointment appointment)
    {
        appointment.Status = AppointmentStatus.Completed;
    }
}