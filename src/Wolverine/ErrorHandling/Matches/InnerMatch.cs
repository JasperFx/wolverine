using System;

namespace Wolverine.ErrorHandling.Matches;

internal class InnerMatch : IExceptionMatch
{
    private readonly IExceptionMatch _inner;

    public InnerMatch(IExceptionMatch inner)
    {
        _inner = inner;
    }

    public string Description => "Inner: " + _inner.Description;

    public bool Matches(Exception ex)
    {
        return ex.InnerException != null && _inner.Matches(ex.InnerException);
    }
}