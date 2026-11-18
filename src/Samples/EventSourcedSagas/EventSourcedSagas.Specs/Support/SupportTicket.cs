using Wolverine;

namespace EventSourcedSagas.Support;

// GH-5415: a saga with no business event for its end. Closing the ticket calls MarkCompleted(), and Wolverine appends
// its SagaCompleted event. Nothing reads support tickets asynchronously, so the stream is archived as soon as the ticket
// completes ([ArchiveWhenCompleted]) instead of waiting for the retention sweep.

public record OpenTicket(Guid SupportTicketId, string Subject);

public record CloseTicket(Guid SupportTicketId);

public record TicketOpened(string Subject);

#region sample_event_sourced_saga_completed_by_MarkCompleted
[ArchiveWhenCompleted]
public class SupportTicket : EventSourcedSaga
{
    public Guid Id { get; set; }
    public string Subject { get; set; } = "";

    public static TicketOpened Start(OpenTicket open) => new(open.Subject);

    public void Handle(CloseTicket close) => MarkCompleted();

    public void Apply(TicketOpened e) => Subject = e.Subject;
}
#endregion
