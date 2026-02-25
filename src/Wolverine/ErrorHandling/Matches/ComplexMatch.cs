using JasperFx.Core;

namespace Wolverine.ErrorHandling.Matches;

internal class ComplexMatch : IExceptionMatch
{
    public readonly List<IExceptionMatch> Excludes = new();
    public readonly List<IExceptionMatch> Includes = new();

    public string Description =>
        $"Include {Includes.Select(x => x.Description).Join(", ")}, exclude {Excludes.Select(x => x.Description).Join(", ")}";

    public bool Matches(Exception ex)
    {
        if (Includes.Count != 0)
        {
            var included = false;
            foreach (var include in Includes)
            {
                if (include.Matches(ex))
                {
                    included = true;
                    break;
                }
            }

            if (!included) return false;
        }

        foreach (var exclude in Excludes)
        {
            if (exclude.Matches(ex)) return false;
        }

        return true;
    }

    public bool IsEmpty()
    {
        return Includes.Count == 0 &&
               Excludes.Count == 0;
    }

    public IExceptionMatch Reduce()
    {
        return IsEmpty() ? new AlwaysMatches() : this;
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