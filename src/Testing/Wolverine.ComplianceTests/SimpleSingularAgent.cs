#region sample_SimpleSingularAgent

using JasperFx.Core;
using Wolverine.Runtime.Agents;

namespace Wolverine.ComplianceTests;

public class SimpleSingularAgent : SingularAgent
{
    private CancellationTokenSource _cancellation = new();
    private Timer _timer;

    // The scheme argument is meant to be descriptive and
    // your agent will have the Uri {scheme}:// in all diagnostics
    // and node assignment storage
    public SimpleSingularAgent() : base("simple")
    {
        
    }

    // This template method should be used to start up your background service
    protected override Task startAsync(CancellationToken cancellationToken)
    {
        _cancellation = new();
        _timer = new Timer(execute, null, 1.Seconds(), 5.Seconds());
        return Task.CompletedTask;
    }

    private void execute(object? state)
    {
        // Do something...
    }

    // This template method should be used to cleanly stop up your background service
    protected override Task stopAsync(CancellationToken cancellationToken)
    {
        _timer.SafeDispose();
        return Task.CompletedTask;
    }
}

#endregion