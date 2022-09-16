using System;
using System.Collections.Generic;
using System.Linq;
using Baseline;

namespace Wolverine.ErrorHandling.Matches;

internal class OrMatch : IExceptionMatch
{
    public readonly List<IExceptionMatch> Inners = new();

    public OrMatch(params IExceptionMatch[] matches)
    {
        Inners.AddRange(matches);
    }

    public string Description => Inners.Select(x => ExceptionMatchExtensions.Formatted(x)).Join(" or ");

    public bool Matches(Exception ex)
    {
        return Inners.Any(x => x.Matches(ex));
    }
}
