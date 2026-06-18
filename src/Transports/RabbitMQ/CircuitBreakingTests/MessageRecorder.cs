using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CircuitBreakingTests;

public class MessageRecorder(ILogger<MessageRecorder> logger) : IAsyncDisposable
{
    private TaskCompletionSource<int> _completion = null!;
    private readonly ConcurrentDictionary<Guid, int> _processedIds = new();
    private readonly ConcurrentBag<Guid> _publishedIds = [];
    private int _expected;
    private CancellationTokenSource? _timeoutCts;

    public bool NeverFail { get; set; }

    public void TrackPublished(Guid id)
    {
        _publishedIds.Add(id);
    }

    public Task WaitForMessagesToBeProcessed(int number, TimeSpan timeout)
    {
        NeverFail = false;
        _processedIds.Clear();
        _publishedIds.Clear();
        _completion = new TaskCompletionSource<int>();
        _expected = number;

        _timeoutCts = new CancellationTokenSource(timeout);
        _timeoutCts.Token.Register(() =>
        {
            int uniqueCount = 0;
            int publishedCount = 0;
            var missing = new List<Guid>();
            uniqueCount = _processedIds.Count;
            publishedCount = _publishedIds.Count;
            missing = [.. _publishedIds.Except(_processedIds.Keys)];
            var sample = string.Join(", ", missing.Take(10));
            logger.LogDebug("DIAG: Processed {uniqueCount}/{publishedCount} published messages", uniqueCount, publishedCount);
            if (missing.Any())
                logger.LogDebug("DIAG: {missingCount} never processed. Sample: {sample}", missing.Count, sample);

            _completion.TrySetException(new TimeoutException(
                $"Listener did not process the expected message count {number} in the time allowed. " +
                $"Only {uniqueCount} unique messages received. " +
                $"{missing.Count} of {publishedCount} published never made it."));
        });

        return _completion.Task;
    }

    public void Increment(Guid messageId)
    {
        _processedIds.AddOrUpdate(messageId, 1, (_, existing) => existing + 1);
        var attempt = _processedIds[messageId];
        var uniqueCount = _processedIds.Count;

        logger.LogDebug("msg#{Id} completed (attempt {attempt}, unique: {uniqueCount})",
            messageId, attempt, uniqueCount);

        if (uniqueCount >= _expected)
            _completion.TrySetResult(uniqueCount);
    }

    public async ValueTask DisposeAsync()
    {
        if (_timeoutCts != null)
        {
            await _timeoutCts.CancelAsync();
            _timeoutCts?.Dispose();
            _timeoutCts = null;
        }
    }
}