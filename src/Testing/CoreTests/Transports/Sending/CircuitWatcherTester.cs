using JasperFx.Core;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace CoreTests.Transports.Sending;

public class CircuitWatcherTester
{
    //[Fact]  //TODO -- CI doesn't like this one sometimes.
    public void ping_until_connected()
    {
        var completed = new ManualResetEvent(false);

        var watcher = new CircuitWatcher(new StubCircuit(5, completed), default);


        completed.WaitOne(1.Seconds())
            .ShouldBeTrue();
    }
}

public class StubCircuit : ISenderCircuit
{
    private readonly ManualResetEvent _completed;
    private readonly int _failureCount;

    public readonly IList<Envelope> Queued = new List<Envelope>();

    private int _count;

    public StubCircuit(int failureCount, ManualResetEvent completed)
    {
        _failureCount = failureCount;
        _completed = completed;
    }

    public Uri Destination => TransportConstants.LocalUri;

    public int QueuedCount => 0;

    public bool Latched { get; private set; }

    public bool SupportsNativeScheduledSend => true;

    public Task<bool> TryToResumeAsync(CancellationToken cancellationToken)
    {
        _count++;

        if (_count < _failureCount)
        {
            throw new Exception("No!");
        }

        return Task.FromResult(true);
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        _completed.Set();
        return Task.CompletedTask;
    }

    public TimeSpan RetryInterval { get; } = 50.Milliseconds();

    public void Dispose()
    {
    }

    public void Start(ISenderCallback callback)
    {
    }

    public Task Enqueue(Envelope envelope)
    {
        Queued.Add(envelope);
        return Task.CompletedTask;
    }

    public Task LatchAndDrain()
    {
        Latched = false;
        return Task.CompletedTask;
    }

    public void Unlatch()
    {
        Latched = true;
    }
}