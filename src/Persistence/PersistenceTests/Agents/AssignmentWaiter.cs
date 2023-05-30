using Microsoft.Extensions.Hosting;
using Wolverine.Logging;
using Wolverine.Tracking;

namespace PersistenceTests.Agents;

internal class AssignmentWaiter : IObserver<IWolverineEvent>
{
    private readonly TaskCompletionSource<bool> _completion = new();
    
    private IDisposable _unsubscribe;
    private readonly WolverineTracker _tracker;

    public Dictionary<Guid, int> AgentCountByHost { get; } = new();

    public AssignmentWaiter(IHost leader)
    {
        _tracker = leader.GetRuntime().Tracker;
    }

    public void ExpectRunningAgents(IHost host, int runningCount)
    {
        var id = host.GetRuntime().Options.UniqueNodeId;
        AgentCountByHost[id] = runningCount;
    }

    public Task<bool> Start(TimeSpan timeout)
    {
        if (HasReached()) return Task.FromResult(true);
        
        _unsubscribe = _tracker.Subscribe(this);
        
        var timeout1 = new CancellationTokenSource(timeout);
        timeout1.Token.Register(() =>
        {
            _completion.TrySetException(new TimeoutException(
                "Did not reach the expected state or message in time"));
        });


        return _completion.Task;
    }

    public bool HasReached()
    {
        foreach (var pair in AgentCountByHost)
        {
            var runningCount = _tracker.Agents.Where(x => !x.Key.Scheme.StartsWith("wolverine")).Count(x => x.Value == pair.Key);
            if (pair.Value != runningCount) return false;
        }

        return true;
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
        _completion.SetException(error);
    }

    public void OnNext(IWolverineEvent value)
    {
        if (HasReached())
        {
            _completion.TrySetResult(true);
            _unsubscribe.Dispose();
        }
    }
}