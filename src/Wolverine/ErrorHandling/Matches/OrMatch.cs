using JasperFx.Core;

namespace Wolverine.ErrorHandling.Matches;

internal class OrMatch : IExceptionMatch
{
    public readonly List<IExceptionMatch> Inners = new();

    public OrMatch(params IExceptionMatch[] matches)
    {
        Inners.AddRange(matches);
    }

    public string Description => Inners.Select(x => x.Formatted()).Join(" or ");

    public bool Matches(Exception ex)
    {
        foreach (var inner in Inners)
        {
            if (inner.Matches(ex)) return true;
        }

        return false;
    }
}