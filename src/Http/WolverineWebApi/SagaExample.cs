using JasperFx.Core;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi;

public record StartReservation(string ReservationId);

public record ReservationBooked(string ReservationId, DateTimeOffset Time);

public static class ReservationEndpoint
{
    [WolverinePost("/reservation")]
    public static (ReservationBooked, Reservation, ReservationTimeout) Post(StartReservation start)
    {
        return (new ReservationBooked(start.ReservationId, DateTimeOffset.UtcNow), new Reservation { Id = start.ReservationId }, new ReservationTimeout(start.ReservationId));
    }
}

public record BookReservation(string Id);

public record ReservationTimeout(string Id) : TimeoutMessage(1.Minutes());

public class Reservation : Saga
{
    public string? Id { get; set; }
    
    // Apply the CompleteReservation to the saga
    public void Handle(BookReservation book, ILogger<Reservation> logger)
    {
        logger.LogInformation("Completing Reservation {Id}", book.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }

    // Delete this Reservation if it has not already been deleted to enforce a "timeout"
    // condition
    public void Handle(ReservationTimeout timeout, ILogger<Reservation> logger)
    {
        logger.LogInformation("Applying timeout to Reservation {Id}", timeout.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }
}