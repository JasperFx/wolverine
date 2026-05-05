namespace Wolverine.ComplianceTests.ErrorHandling.Faults;

public class FaultSink
{
    public List<Fault<OrderPlaced>> Captured { get; } = new();
}
