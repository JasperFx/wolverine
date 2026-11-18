using Wolverine;

namespace EventSourcedSagas.Mistakes;

// GH-5415: event-sourced sagas Wolverine refuses. SupportTicket runs in the fulfillment service and is refused at run
// time; the others are each started in a service of their own, which refuses to start.

// ---- completing without a terminal event (refused at run time) ---------------------------------------------------------

public record OpenTicket(Guid SupportTicketId, string Subject);

public record CloseTicketQuietly(Guid SupportTicketId);

public record TicketOpened(string Subject);

public class SupportTicket : EventSourcedSaga
{
    public Guid Id { get; set; }
    public string Subject { get; set; } = "";

    public static TicketOpened Start(OpenTicket open) => new(open.Subject);

    // The mistake: the ticket completes, but no event says so. The next load of the stream would find it open again
    public void Handle(CloseTicketQuietly close) => MarkCompleted();

    public void Apply(TicketOpened e) => Subject = e.Subject;
}

// ---- identities an event-sourced saga cannot have (refused at startup) ---------------------------------------------------

public record StartIntKeyedFulfillment(int IntKeyedFulfillmentId);

public record IntKeyedFulfillmentStarted(int Number);

public class IntKeyedFulfillment : EventSourcedSaga
{
    public int Id { get; set; }

    public static IntKeyedFulfillmentStarted Start(StartIntKeyedFulfillment command) => new(command.IntKeyedFulfillmentId);

    public void Apply(IntKeyedFulfillmentStarted e) => Id = e.Number;
}

public record StartStringKeyedFulfillment(string StringKeyedFulfillmentId);

public record StringKeyedFulfillmentStarted(string Key);

// A string id is fine on a store keyed by string; this one is used with a store keyed by Guid
public class StringKeyedFulfillment : EventSourcedSaga
{
    public string Id { get; set; } = "";

    public static StringKeyedFulfillmentStarted Start(StartStringKeyedFulfillment command) => new(command.StringKeyedFulfillmentId);

    public void Apply(StringKeyedFulfillmentStarted e) => Id = e.Key;
}

// ---- a strong-typed id over a Guid (accepted) --------------------------------------------------------------------------

public record struct ParcelId(Guid Value)
{
    public static bool TryParse(string? input, out ParcelId result)
    {
        if (Guid.TryParse(input, out var guid))
        {
            result = new ParcelId(guid);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

public record ShipParcel(ParcelId ParcelId);

public record ParcelShipped(ParcelId ParcelId);

public class Parcel : EventSourcedSaga
{
    public ParcelId Id { get; set; }

    public static ParcelShipped Start(ShipParcel command) => new(command.ParcelId);

    public void Apply(ParcelShipped e) => Id = e.ParcelId;
}
