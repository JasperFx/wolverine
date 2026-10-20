using Microsoft.Extensions.Hosting;
using Wolverine.Logging;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
namespace Wolverine.ComplianceTests;

/// <summary>
///     Writes every <see cref="IWolverineEvent" /> a host's tracker publishes to test output.
/// </summary>
/// <remarks>
///     The tracker carries <c>ListenerState</c> changes only. GH-5078: this used to have a branch for
///     <c>AgentAssignmentsChanged</c>, which nothing in Wolverine publishes any more, so it never ran. Agent
///     assignment history comes from <see cref="IWolverineObserver" /> instead: the leadership suite's
///     <c>Telemetry.AgentAssignmentTelemetry</c> wraps the runtime's observer before the host starts and is written
///     out when a specification fails.
/// </remarks>
public class XUnitEventObserver : IObserver<IWolverineEvent>
{
    private readonly ITestOutputHelper _output;
    private readonly int _assignedId;

    public XUnitEventObserver(IHost host, ITestOutputHelper output)
    {
        _output = output;
        var runtime = host.GetRuntime();

        _assignedId = runtime.Options.Durability.AssignedNodeNumber;

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
        _output.WriteLine($"Host {_assignedId}: {value}");
    }
}