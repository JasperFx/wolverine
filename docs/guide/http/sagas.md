# Integration with Sagas

Http endpoints can start [Wolverine sagas](/guide/durability/sagas) by just using a return value for a `Saga` value. 

Let's say that we have a stateful saga type for making online reservations like this:

<!-- snippet: sample_reservation_saga -->
<a id='snippet-sample_reservation_saga'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/SagaExample.cs#L62-L88' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_reservation_saga' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To start the `Reservation` saga, you could use an HTTP endpoint method like this one:

<!-- snippet: sample_starting_saga_from_http_endpoint -->
<a id='snippet-sample_starting_saga_from_http_endpoint'></a>
```cs
[WolverinePost("/reservation")]
public static (
    // The first return value would be written out as the HTTP response body
    ReservationBooked, 
    
    // Because this subclasses from Saga, Wolverine will persist this entity
    // with saga persistence
    Reservation, 
    
    // Other return values that trigger no special handling will be treated
    // as cascading messages
    ReservationTimeout) Post(StartReservation start)
{
    return (new ReservationBooked(start.ReservationId, DateTimeOffset.UtcNow), new Reservation { Id = start.ReservationId }, new ReservationTimeout(start.ReservationId));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/SagaExample.cs#L14-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_starting_saga_from_http_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

