using JasperFx.Core;

namespace Wolverine.Transports.Sending;

internal interface ICircuitTester
{
    Task<bool> TryToResumeAsync(CancellationToken cancellationToken);
}

internal interface ISenderCircuit : ICircuitTester
{
    TimeSpan RetryInterval { get; }
    Task ResumeAsync(CancellationToken cancellationToken);
}

internal class CircuitWatcher : IDisposable
{
    // Linked (not just stored) so Dispose() can stop the loop on its own, independent of whether the
    // caller's token has been cancelled yet. Without this, Dispose() only released the Task wrapper --
    // pingUntilConnectedAsync kept running against the still-live caller token.
    private readonly CancellationTokenSource _cancellation;
    private readonly ISenderCircuit _senderCircuit;
    private readonly Task _task;

    public CircuitWatcher(ISenderCircuit senderCircuit, CancellationToken cancellation)
    {
        _senderCircuit = senderCircuit;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        _task = Task.Run(pingUntilConnectedAsync, _cancellation.Token);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
        _cancellation.Dispose();
    }

    private async Task pingUntilConnectedAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_senderCircuit.RetryInterval);
            while (await timer.WaitForNextTickAsync(_cancellation.Token))
            {
                try
                {
                    var pinged = await _senderCircuit.TryToResumeAsync(_cancellation.Token);

                    if (pinged)
                    {
                        await _senderCircuit.ResumeAsync(_cancellation.Token);
                        return;
                    }
                }
                // ReSharper disable once EmptyGeneralCatchClause
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
            // Expected on Dispose(): cancelling and disposing the linked CTS can surface as
            // OperationCanceledException from the timer wait, or ObjectDisposedException if a
            // token registration races the dispose. Either way, shutting down is correct --
            // don't let this show up as an unobserved/faulted background task.
        }
    }
}