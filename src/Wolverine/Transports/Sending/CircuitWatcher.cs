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
    private readonly CancellationToken _cancellation;
    private readonly ISenderCircuit _senderCircuit;
    private readonly Task _task;

    public CircuitWatcher(ISenderCircuit senderCircuit, CancellationToken cancellation)
    {
        _senderCircuit = senderCircuit;
        _cancellation = cancellation;

        _task = Task.Run(pingUntilConnectedAsync, _cancellation);
    }

    public void Dispose()
    {
        _task.SafeDispose();
    }

    private async Task pingUntilConnectedAsync()
    {
        using var timer=new PeriodicTimer(_senderCircuit.RetryInterval);
        while (await timer.WaitForNextTickAsync(_cancellation))
        {
            try
            {
                var pinged = await _senderCircuit.TryToResumeAsync(_cancellation);

                if (pinged)
                {
                    await _senderCircuit.ResumeAsync(_cancellation);
                    return;
                }
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch (Exception)
            {
            }
        }
    }
}