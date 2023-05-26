using System;

namespace Wolverine.ErrorHandling.Matches;

public interface IExceptionMatch
{
    string Description { get; }

    bool Matches(Exception ex);
}