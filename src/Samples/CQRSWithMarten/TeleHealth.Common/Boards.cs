namespace TeleHealth.Common;

internal interface BoardStateEvent;

public record BoardOpened(string Name, DateOnly Date, DateTimeOffset Opened) : BoardStateEvent;

public record BoardFinished(DateTimeOffset Timestamp) : BoardStateEvent;

public record BoardClosed(DateTimeOffset Timestamp, string Reason) : BoardStateEvent;

public class Board
{
    public Board()
    {
    }

    public Board(BoardOpened opened)
    {
        Name = opened.Name;
        Activated = opened.Opened;
        Date = opened.Date;
    }

    public Guid Id { get; set; }
    public string Name { get; }
    public DateTimeOffset Activated { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset? Closed { get; set; }

    public string CloseReason { get; private set; }

    public void Apply(BoardFinished finished)
    {
        Finished = finished.Timestamp;
    }

    public void Apply(BoardClosed closed)
    {
        Closed = closed.Timestamp;
        CloseReason = closed.Reason;
    }
}