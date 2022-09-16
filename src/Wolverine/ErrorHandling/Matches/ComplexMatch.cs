using System;
using System.Collections.Generic;
using System.Linq;
using Baseline;

namespace Wolverine.ErrorHandling.Matches;

internal class ComplexMatch : IExceptionMatch
{
    public readonly List<IExceptionMatch> Includes = new();
    public readonly List<IExceptionMatch> Excludes = new();

    public bool IsEmpty() => !Includes.Any() && !Excludes.Any();

    public IExceptionMatch Reduce() => IsEmpty() ? new AlwaysMatches() : this;

    public string Description =>
        $"Include {Includes.Select(x => x.Description).Join(", ")}, exclude {Excludes.Select(x => x.Description).Join(", ")}";
    public bool Matches(Exception ex)
    {
        if (Includes.Any())
        {
            return Includes.Any(x => x.Matches(ex)) && !(Excludes.Any(x => x.Matches(ex)));
        }

        return !(Excludes.Any(x => x.Matches(ex)));
    }

    public ComplexMatch Exclude<T>(Func<T, bool>? filter = null) where T : Exception
    {
        Excludes.Add(filter == null ? new TypeMatch<T>() : new UserSupplied<T>(filter));
        return this;
    }

    public ComplexMatch Include<T>(Func<T, bool>? filter = null) where T : Exception
    {
        Includes.Add(filter == null ? new TypeMatch<T>() : new UserSupplied<T>(filter));
        return this;
    }
}
