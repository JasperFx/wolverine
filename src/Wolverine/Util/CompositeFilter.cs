using System;
using System.Collections.Generic;
using System.Linq;

namespace Wolverine.Util;

public class CompositeFilter<T>
{
    public CompositePredicate<T> Includes { get; set; } = new();
    public CompositePredicate<T> Excludes { get; set; } = new();

    public bool Matches(T target)
    {
        return Includes.MatchesAny(target) && Excludes.DoesNotMatchAny(target);
    }
}

public class CompositePredicate<T>
{
    private readonly List<Func<T, bool>> _list = new();
    private Func<T, bool> _matchesAll = _ => true;
    private Func<T, bool> _matchesAny = _ => true;
    private Func<T, bool> _matchesNone = _ => false;

    public void Add(Func<T, bool> filter)
    {
        _matchesAll = x => _list.All(predicate => predicate(x));
        _matchesAny = x => _list.Any(predicate => predicate(x));
        _matchesNone = x => !MatchesAny(x);

        _list.Add(filter);
    }

    public static CompositePredicate<T> operator +(CompositePredicate<T> invokes, Func<T, bool> filter)
    {
        invokes.Add(filter);
        return invokes;
    }

    public bool MatchesAll(T target)
    {
        return _matchesAll(target);
    }

    public bool MatchesAny(T target)
    {
        return _matchesAny(target);
    }

    public bool MatchesNone(T target)
    {
        return _matchesNone(target);
    }

    public bool DoesNotMatchAny(T target)
    {
        return _list.Count == 0 || !MatchesAny(target);
    }
}
