namespace KafkaPerfRig;

/// <summary>
/// Both flows carry the publish-call timestamp (Stopwatch.GetTimestamp ticks) in the body so the
/// native and Wolverine twins are stamped identically. The monotonic clock is machine-wide on
/// macOS/Linux, and the whole rig runs on one box, so cross-process tick deltas are valid.
/// </summary>
public class SmallEvent
{
    public string GameId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public long T0 { get; set; }
    public bool Warmup { get; set; }
    public string Payload { get; set; } = string.Empty;
}

public class LargeEvent
{
    public string GameId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public long T0 { get; set; }
    public bool Warmup { get; set; }
    public string Payload { get; set; } = string.Empty;
}

public static class Payloads
{
    // Sized so the serialized JSON lands near the client's 1Kb / 100Kb payloads
    public static readonly string Small = new('x', 900);
    public static readonly string Large = new('x', 100_000);
}
