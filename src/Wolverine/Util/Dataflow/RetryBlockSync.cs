using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Util.Dataflow;

/// <summary>
/// Synchrous version of RetryBlock - this version is retrying on the same thread context so execution os sending message is waiting for information that message was sent to the broker or there was a problem
/// </summary>
/// <typeparam name="T"></typeparam>
public class RetryBlockSync<T>(Func<T, CancellationToken, Task> handler, ILogger logger, CancellationToken cancellationToken) : IRetryBlock<T>
{
    private readonly CancellationToken _cancellationToken = cancellationToken;
    private readonly Func<T, CancellationToken, Task> _handler = handler;
    private readonly ILogger _logger = logger;
    public TimeSpan[] Pauses { get; set; } = [0.Milliseconds(), 50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds()];

    public async Task PostAsync(T message)
    {
        if (_cancellationToken.IsCancellationRequested) return;

        for (var attempt = 1; attempt <= Pauses.Length; attempt++)
        {
            var delay = Pauses[attempt-1];
            try
            {
                if (attempt > 1 && delay.TotalMilliseconds > 0)
                {
                    await Task.Delay(delay,  _cancellationToken).ConfigureAwait(false);
                }

                await _handler(message, _cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Completed {Item}", message);
                return;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Operation canceled for message {Message} on attempt {Attempts}", message, attempt);
                throw;
            }
            catch (Exception e)
            {
                if (attempt < Pauses.Length)
                {
                    _logger.LogInformation(e, "Retrying message {Message} after {Attempts} attempts", message, attempt);
                }
                else
                {
                    _logger.LogError(e, "Error proccessing message {Message} on attempt {Attempts}", message, attempt);
                    throw;
                }
            }
        }
    }
}