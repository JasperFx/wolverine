using Wolverine.Attributes;
using Marten;

namespace CommandBusSamples;

public record AddReservation(string RestaurantName, DateTime Time);
public record ConfirmReservation(Guid ReservationId);
public record ReservationAdded(Guid ReservationId);
public record ReservationConfirmed(Guid ReservationId);

public class Reservation
{
    public Guid Id { get; set; }
    public DateTimeOffset Time { get; set; }
    public string RestaurantName { get; set; }
    public bool IsConfirmed { get; set; }
}

public static class AddReservationHandler
{
    [Transactional]
    public static  ReservationAdded Handle(AddReservation command, IDocumentSession session)
    {
        var reservation = new Reservation{
            Id = Guid.NewGuid(),
            Time = DateTime.Today.AddHours(18).AddMinutes(30),  // command.Time,
            RestaurantName = command.RestaurantName,
            IsConfirmed = false
        };
        session.Store(reservation);
        return new ReservationAdded(reservation.Id);
    }
}

[LocalQueue("Notifications")]
[RetryNow(typeof(HttpRequestException), 50, 100, 250)]
public class ReservationAddedHandler
{
    public async Task Handle(ReservationAdded added, IQuerySession session)
    {
        // add some interesting code here...
        Console.WriteLine($"Apparently a new reservation with ID=${added.ReservationId} got added");
        // simulate some work
        await Task.Delay(200);
    }
}

public static class ConfirmReservationHandler
{
    [Transactional]
    public static async Task<ReservationConfirmed> Handle(ConfirmReservation command, IDocumentSession session)
    {
        var reservation = await session.LoadAsync<Reservation>(command.ReservationId);

        reservation!.IsConfirmed = true;

        session.Store(reservation);

        // Kicking out a "cascaded" message
        return new ReservationConfirmed(reservation.Id);
    }
}

// Just assume this service is in the IoC container
public interface IRestaurantProxy
{
    Task NotifyRestaurant(Reservation? reservation);
}

public class RealRestaurantProxy : IRestaurantProxy
{
    public Task NotifyRestaurant(Reservation? reservation)
    {
        Console.WriteLine("Sending the reservation to a restaurant");
        return Task.CompletedTask;
    }
}

// What about error handling?
[LocalQueue("Notifications")]
[RetryNow(typeof(HttpRequestException), 50, 100, 250)]
public class ReservationConfirmedHandler
{
    public async Task Handle(ReservationConfirmed confirmed, IQuerySession session, IRestaurantProxy restaurant)
    {
        var reservation = await session.LoadAsync<Reservation>(confirmed.ReservationId);

        // Make a call to an external web service through a proxy
        await restaurant.NotifyRestaurant(reservation);
    }
}
