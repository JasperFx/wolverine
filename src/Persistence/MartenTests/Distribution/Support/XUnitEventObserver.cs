using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit.Abstractions;

namespace MartenTests.Distribution.Support;

public class XUnitEventObserver : IObserver<IWolverineEvent>
{
    private readonly ITestOutputHelper _output;
    private readonly int _assignedId;

    public XUnitEventObserver(IHost host, ITestOutputHelper output)
    {
        _output = output;
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

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
        if (value is AgentAssignmentsChanged changed)
        {
            _output.WriteLine($"Host {_assignedId}: Agent assignments determined for known nodes {changed.Assignments.Nodes.Select(x => x.ToString()).Join(", ")}");
            if (!changed.Commands.Any()) _output.WriteLine("No assignment changes detected");

            foreach (var agent in changed.Assignments.AllAgents)
            {
                if (agent.AssignedNode == null)
                {
                    _output.WriteLine($"* {agent.Uri} is not assigned");
                }
                else if (agent.OriginalNode == null)
                {
                    _output.WriteLine($"* {agent.Uri} assigned to node {agent.AssignedNode.AssignedId}");
                }
                else if (agent.OriginalNode == agent.AssignedNode)
                {
                    _output.WriteLine($"* {agent.Uri} is unchanged on node {agent.AssignedNode.AssignedId}");
                }
                else
                {
                    _output.WriteLine($"* {agent.Uri} reassigned from node {agent.OriginalNode.AssignedId} to node {agent.AssignedNode.AssignedId}");
                }
            }
        }
        else
        {
            _output.WriteLine($"Host {_assignedId}: {value}");
        }
    }
}