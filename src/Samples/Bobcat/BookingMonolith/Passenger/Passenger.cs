namespace Passenger;

public class Passenger
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PassportNumber { get; set; } = string.Empty;
    public PassengerType Type { get; set; } = PassengerType.Unknown;
    public int Age { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum PassengerType { Unknown, Male, Female, Baby, Child }
