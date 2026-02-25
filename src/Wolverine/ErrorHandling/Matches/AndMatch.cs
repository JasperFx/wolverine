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
        foreach (var inner in Inners)
        {
            if (!inner.Matches(ex)) return false;
        }

        return true;
    }

    public string Description => Inners.Select(x => x.Formatted()).Join(" and ");
}