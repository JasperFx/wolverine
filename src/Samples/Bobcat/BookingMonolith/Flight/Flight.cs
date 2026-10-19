namespace Flight;

public class Flight
{
    public Guid Id { get; set; }
    public string FlightNumber { get; set; } = string.Empty;
    public Guid AircraftId { get; set; }
    public Guid DepartureAirportId { get; set; }
    public Guid ArriveAirportId { get; set; }
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public FlightStatus Status { get; set; } = FlightStatus.Active;
    public DateTime DepartureDate { get; set; }
    public DateTime ArriveDate { get; set; }
    public DateTime FlightDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum FlightStatus { Active, Completed, Cancelled, Delayed }

public class Aircraft
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int ManufacturingYear { get; set; }
}

public class Airport
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
}

public class Seat
{
    public Guid Id { get; set; }
    public string SeatNumber { get; set; } = string.Empty;
    public Guid FlightId { get; set; }
    public SeatType Type { get; set; } = SeatType.Window;
    public SeatClass Class { get; set; } = SeatClass.Economy;
    public bool IsReserved { get; set; }
}

public enum SeatType { Window, Middle, Aisle }
public enum SeatClass { Economy, Business, FirstClass }
