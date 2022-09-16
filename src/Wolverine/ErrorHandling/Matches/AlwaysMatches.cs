using System;

namespace Wolverine.ErrorHandling.Matches;

internal class AlwaysMatches : IExceptionMatch
{
    public string Description => "All exceptions";

    public bool Matches(Exception ex)
    {
        return true;
    }
}
