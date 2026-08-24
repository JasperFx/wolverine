using JasperFx.Core;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

public class FailureSlot
{
    private readonly List<IContinuationSource> _sources = new();

    public FailureSlot(int attempt, IContinuationSource source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        Attempt = attempt;
        _sources.Add(source);
    }

    public int Attempt { get; }

    public void AddAdditionalSource(IContinuationSource source)
    {
        _sources.Add(source);
    }

    public void InsertSourceAtTop(IContinuationSource source)
    {
        _sources.Insert(0, source);
    }

    internal bool ApplyJitter(IJitterStrategy strategy)
    {
        var applied = false;
        foreach (var source in _sources)
        {
            if (source is IJitterable jitterable && jitterable.TrySetJitter(strategy))
            {
                applied = true;
            }
        }
        return applied;
    }

    /// <summary>
    /// Build the continuation for this attempt, or null if every source in this slot declined the
    /// envelope. See <see cref="IContinuationSource.Build" /> for what declining means.
    /// </summary>
    public IContinuation? Build(Exception ex, Envelope envelope)
    {
        if (_sources.Count == 1)
        {
            return _sources[0].Build(ex, envelope);
        }

        var continuations = new List<IContinuation>(_sources.Count);
        foreach (var source in _sources)
        {
            if (source.Build(ex, envelope) is { } continuation)
            {
                continuations.Add(continuation);
            }
        }

        return continuations.Count switch
        {
            0 => null,
            1 => continuations[0],
            _ => new CompositeContinuation(continuations.ToArray())
        };
    }

    public string Describe()
    {
        return _sources.Select(x => x.Description).Join(", then ");
    }
}