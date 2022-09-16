using System;
using System.Collections.Generic;
using System.Linq;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal class FailureSlot
{
    public int Attempt { get; }
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

    public void AddAdditionalSource(IContinuationSource source)
    {
        _sources.Add(source);
    }

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return _sources.Count == 1
            ? _sources.Single().Build(ex, envelope)
            : new CompositeContinuation(_sources.Select(x => x.Build(ex, envelope)).ToArray());
    }
}
