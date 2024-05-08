using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Marten.Events.Projections;

namespace TeleHealth.Common;

public class BoardViewProjection : ExperimentalMultiStreamProjection<BoardView, Guid>
{
    public BoardViewProjection()
    {
        DeleteEvent<BoardFinished>();
    }

    protected override ValueTask GroupEvents(IEventGrouping<Guid> grouping, IQuerySession session, List<IEvent> events)
    {
        //grouping.AddEvents<IBoardEvent>(x => x.BoardId, events);
        grouping.AddEventsWithMetadata<IEvent<BoardStateEvent>>(x => x.StreamId, events);

        return ValueTask.CompletedTask;
    }

    // Using event metadata
    public BoardView Create(IEvent<BoardOpened> opened)
    {
        return new BoardView
        {
            Name = opened.Data.Name,
            Date = opened.Data.Date,
            Opened = opened.Timestamp
        };
    }

    public void Apply(IEvent<BoardClosed> closed, BoardView view)
    {
        view.Closed = closed.Timestamp;
        view.CloseReason = closed.Data.Reason;
    }

    public async Task Apply(IEvent<AppointmentRouted> @event, BoardView view, IQuerySession session)
    {
        var routed = @event.Data;
        var patient = await session.LoadAsync<Patient>(routed.PatientId);
        var appointment = new BoardAppointment
        {
            PatientName = $"{patient.FirstName} {patient.LastName}",
            OriginalEstimatedTime = routed.EstimatedTime,
            CurrentEstimatedTime = routed.EstimatedTime,
            Status = AppointmentStatus.Requested,
            AppointmentId = @event.StreamId
        };

        view.Appointments.Add(appointment);
    }

    public void Apply(IEvent<AppointmentScheduled> scheduled, BoardView view)
    {
        var appointment = view.FindAppointment(scheduled.StreamId);
        appointment.Status = AppointmentStatus.Scheduled;
        appointment.ProviderId = scheduled.Data.ProviderId;
        appointment.CurrentEstimatedTime = scheduled.Data.EstimatedTime;

        var provider = view.FindProvider(appointment.ProviderId!.Value);
        provider.Status = ProviderStatus.Assigned;
    }

    public void Apply(IEvent<AppointmentStarted> started, BoardView view)
    {
        var appointment = view.FindAppointment(started.StreamId);
        appointment.Status = AppointmentStatus.Started;
        appointment.Started = started.Timestamp;

        var provider = view.FindProvider(appointment.ProviderId!.Value);
        provider.Status = ProviderStatus.Ready;
    }

    public void Apply(IEvent<AppointmentFinished> finished, BoardView view)
    {
        var appointment = view.FindAppointment(finished.StreamId);
        appointment.Status = AppointmentStatus.Completed;
        appointment.Finished = finished.Timestamp;

        var provider = view.FindProvider(appointment.ProviderId!.Value);
        provider.Status = ProviderStatus.Ready;
    }

    public async Task Apply(IEvent<ProviderJoined> joined, BoardView view, IQuerySession session)
    {
        var provider = await session.LoadAsync<Provider>(joined.Data.ProviderId);
        var boardProvider = new BoardProvider
        {
            Provider = provider,
            ShiftId = joined.StreamId,
            Status = ProviderStatus.Ready
        };

        view.Providers.Add(boardProvider);
    }

    public void Apply(IEvent<ProviderReady> ready, BoardView view)
    {
        view.FindProviderByShift(ready).Status = ProviderStatus.Ready;
    }

    public void Apply(IEvent<ChartingStarted> @event, BoardView view)
    {
        view.FindProviderByShift(@event).Status = ProviderStatus.Charting;
    }

    public void Apply(IEvent<ChartingFinished> @event, BoardView view)
    {
        //view.RemoveAppointment(@event.Data.AppointmentId);
    }

    public void Apply(IEvent<ProviderPaused> @event, BoardView view)
    {
        view.FindProviderByShift(@event).Status = ProviderStatus.Charting;
    }

    public void Apply(IEvent<ProviderSignedOff> @event, BoardView view)
    {
        view.RemoveProvider(@event.StreamId);
    }
}