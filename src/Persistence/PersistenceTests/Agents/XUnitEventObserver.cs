using Microsoft.Extensions.Hosting;
using Wolverine.Logging;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace PersistenceTests.Agents;

public class XUnitEventObserver : IObserver<IWolverineEvent>
{
    private readonly ITestOutputHelper _output;
    private readonly int _assignedId;

    public XUnitEventObserver(IHost host, ITestOutputHelper output)
    {
        _output = output;
        var runtime = host.GetRuntime();

        _assignedId = runtime.Tracker.Self.AssignedNodeId;

        runtime.Tracker.Subscribe(this);
    }

    public void OnCompleted()
    {
        
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(IWolverineEvent value)
    {
        if (value is AgentAssignmentsChanged changed)
        {
            _output.WriteLine($"Host {_assignedId}: Agent assignments determined");
            foreach (var command in changed.Commands)
            {
                _output.WriteLine($"* {command}");
            }
        }
        else
        {
            _output.WriteLine($"Host {_assignedId}: {value}");
        }
    }
}