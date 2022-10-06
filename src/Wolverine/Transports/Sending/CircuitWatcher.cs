using System;
using System.Threading;
using System.Threading.Tasks;
using Baseline;

namespace Wolverine.Transports.Sending;

internal interface ICircuitTester
{
    Task<bool> TryToResumeAsync(CancellationToken cancellationToken);
}

internal interface ICircuit : ICircuitTester
{
    TimeSpan RetryInterval { get; }
    Task ResumeAsync(CancellationToken cancellationToken);
}

internal class CircuitWatcher : IDisposable
{
    private readonly CancellationToken _cancellation;
    private readonly ICircuit _circuit;
    private readonly Task _task;

    public CircuitWatcher(ICircuit circuit, CancellationToken cancellation)
    {
        _circuit = circuit;
        _cancellation = cancellation;

        _task = Task.Run(pingUntilConnectedAsync, _cancellation);
    }

    public void Dispose()
    {
        _task.SafeDispose();
    }

    private async Task pingUntilConnectedAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            await Task.Delay(_circuit.RetryInterval, _cancellation);

            try
            {
                var pinged = await _circuit.TryToResumeAsync(_cancellation);

                if (pinged)
                {
                    await _circuit.ResumeAsync(_cancellation);
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
