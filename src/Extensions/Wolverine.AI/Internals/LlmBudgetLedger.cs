using System.Diagnostics;

namespace Wolverine.AI.Internals;

/// <summary>
/// Tracks token spend over a trailing window so the budget middleware can refuse callouts once the
/// application has burned through its allowance.
/// </summary>
public interface ILlmBudgetLedger
{
    /// <summary>
    /// Total tokens recorded within the trailing window as of now.
    /// </summary>
    long TokensInWindow();

    /// <summary>
    /// Record what a completed callout actually cost, as reported by the provider.
    /// </summary>
    void Record(long tokens);
}

/// <summary>
/// A single node's view of recent token spend. Deliberately a bounded ring of per second buckets rather
/// than a list of individual entries: the window is coarse anyway, and a busy application must not
/// accumulate one allocation per callout in a structure that is read on every callout.
/// </summary>
internal class LlmBudgetLedger : ILlmBudgetLedger
{
    private readonly long[] _buckets;
    private readonly long[] _bucketSeconds;
    private readonly int _windowSeconds;
    private readonly object _lock = new();

    public LlmBudgetLedger(LlmBudget budget)
    {
        _windowSeconds = Math.Max(1, (int)Math.Ceiling(budget.Window.TotalSeconds));
        _buckets = new long[_windowSeconds];
        _bucketSeconds = new long[_windowSeconds];
    }

    // Stopwatch rather than the wall clock: the ledger only ever compares two of its own readings, and
    // a clock adjustment mid-window would otherwise either strand spend inside the window forever or
    // wipe it early.
    private static long NowSeconds() => Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public long TokensInWindow()
    {
        var now = NowSeconds();
        var oldest = now - _windowSeconds + 1;

        lock (_lock)
        {
            long total = 0;
            for (var i = 0; i < _buckets.Length; i++)
            {
                if (_bucketSeconds[i] >= oldest) total += _buckets[i];
            }

            return total;
        }
    }

    public void Record(long tokens)
    {
        if (tokens <= 0) return;

        var now = NowSeconds();
        var index = (int)(now % _windowSeconds);

        lock (_lock)
        {
            if (_bucketSeconds[index] != now)
            {
                _bucketSeconds[index] = now;
                _buckets[index] = 0;
            }

            _buckets[index] += tokens;
        }
    }
}
