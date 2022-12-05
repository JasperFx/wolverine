using System;
using System.Collections.Generic;
using System.Linq;
using JasperFx.Core;

namespace Wolverine.ErrorHandling.Matches;

internal class AndMatch : IExceptionMatch
{
    public readonly List<IExceptionMatch> Inners = new();

    public AndMatch(params IExceptionMatch[] matches)
    {
        Inners.AddRange(matches);
    }

    public bool Matches(Exception ex)
    {
        return Inners.All(x => x.Matches(ex));
    }

    public string Description => Inners.Select(x => x.Formatted()).Join(" and ");
}