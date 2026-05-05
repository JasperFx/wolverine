namespace Wolverine.ComplianceTests.ErrorHandling.Faults;

public class FaultSinkHandler
{
    public Task Handle(Fault<OrderPlaced> fault, FaultSink sink)
    {
        sink.Captured.Add(fault);
        return Task.CompletedTask;
    }
}
