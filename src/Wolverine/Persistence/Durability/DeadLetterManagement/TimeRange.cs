namespace Wolverine.Persistence.Durability.DeadLetterManagement;

public record TimeRange(DateTimeOffset? From, DateTimeOffset? To)
{
    public static TimeRange AllTime() => new TimeRange(null, null);
}