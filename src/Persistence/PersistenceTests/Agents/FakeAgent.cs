using Wolverine.Runtime.Agents;

namespace PersistenceTests.Agents;

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
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public Uri Uri { get; }
}