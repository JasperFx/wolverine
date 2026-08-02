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

    /// <summary>
    ///     GH-3779: how long this agent takes to start. Defaults to zero, which is what every test written
    ///     before the slow-start work assumed. A real daemon agent is nothing like free — a Marten shard
    ///     replaying behind a projection version bump was measured in the field at p50 27s / p95 82s with a
    ///     215s tail — and every defect in the GH-3753 chain takes a slow start as its precondition, so a
    ///     harness that cannot express one cannot reproduce any of them.
    /// </summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    ///     GH-3779: how long this agent takes to stop. A stop can be as slow as a start — a shard mid-replay
    ///     has to let go of its work — which is why StopRemoteAgents got the same scaled reply window as
    ///     AssignAgents in GH-3748.
    /// </summary>
    public TimeSpan StopDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    ///     Number of times StartAsync has been entered, whether or not it completed. Distinguishes a start
    ///     that is merely slow from one the leader has re-driven.
    /// </summary>
    public int StartCount => _startCount;

    private int _startCount;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);

        if (StartDelay > TimeSpan.Zero)
        {
            await Task.Delay(StartDelay, cancellationToken);
        }

        IsRunning = true;
        Status = AgentStatus.Running;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (StopDelay > TimeSpan.Zero)
        {
            await Task.Delay(StopDelay, cancellationToken);
        }

        IsRunning = false;
        Status = AgentStatus.Stopped;
    }

    public AgentStatus Status { get; private set; } = AgentStatus.Running;

    public Uri Uri { get; }
}
