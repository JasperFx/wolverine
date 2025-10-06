using JasperFx;
using Wolverine.Runtime.Agents;

namespace Wolverine.ComplianceTests;

public class FakeAgent : IAgent
{
    public FakeAgent(Uri uri)
    {
        Uri = uri;
    }

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        Status = AgentStatus.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IsRunning = false;
        Status = AgentStatus.Stopped;
        return Task.CompletedTask;
    }
    
    public AgentStatus Status { get; private set; } = AgentStatus.Running;

    public Uri Uri { get; }
}