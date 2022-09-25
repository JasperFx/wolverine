namespace Wolverine.Logging;

public class PersistedCounts
{
    public int Incoming { get; set; }
    public int Scheduled { get; set; }
    public int Outgoing { get; set; }
    
    public int Handled { get; set; }

    public override string ToString()
    {
        return $"{nameof(Incoming)}: {Incoming}, {nameof(Scheduled)}: {Scheduled}, {nameof(Outgoing)}: {Outgoing}, {nameof(Handled)}: {Handled}";
    }
}
