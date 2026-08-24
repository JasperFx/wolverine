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

    /// <summary>
    /// GH-4060. The continuation sources this slot will build from, so configuration validation can ask what a rule
    /// would actually DO without having to manufacture an exception and an envelope to build it against.
    /// </summary>
    internal IReadOnlyList<IContinuationSource> Sources => _sources;

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

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        if (_sources.Count == 1)
        {
            return _sources[0].Build(ex, envelope);
        }

        var continuations = new IContinuation[_sources.Count];
        for (var i = 0; i < _sources.Count; i++)
        {
            continuations[i] = _sources[i].Build(ex, envelope);
        }

        return new CompositeContinuation(continuations);
    }

    public string Describe()
    {
        return _sources.Select(x => x.Description).Join(", then ");
    }
}