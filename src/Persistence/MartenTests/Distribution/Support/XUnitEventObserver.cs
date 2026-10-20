using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;
namespace MartenTests.Distribution.Support;

/// <summary>
///     Writes every <see cref="IWolverineEvent" /> a host's tracker publishes to test output: in practice
///     <c>ListenerState</c> changes. GH-5078: the agent assignment branch it used to have was dead, because
///     nothing publishes <c>AgentAssignmentsChanged</c> any more. Assignment history is on
///     <see cref="IWolverineObserver" />, which has to be wrapped before the host starts.
/// </summary>
public class XUnitEventObserver : IObserver<IWolverineEvent>, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly int _assignedId;
    private readonly IDisposable _subsriber;

    public XUnitEventObserver(IHost host, ITestOutputHelper output)
    {
        _output = output;
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        _assignedId = runtime.Options.Durability.AssignedNodeNumber;

        _subsriber = runtime.Tracker.Subscribe(this);
    }

    public void Dispose()
    {
        _subsriber?.Dispose();
        GC.SuppressFinalize(this);
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