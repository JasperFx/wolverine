using System;

namespace Wolverine.ErrorHandling.Matches;

internal interface IExceptionMatch
{
    string Description { get; }

    bool Matches(Exception ex);
}