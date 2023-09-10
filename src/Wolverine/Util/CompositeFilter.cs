namespace Wolverine.Util;

[Obsolete("Try to eliminate this, or put back in Jasperfx.core, or add excludes to type filter ")]
internal class CompositePredicate<T>
{
    private readonly List<Func<T, bool>> _list = new List<Func<T, bool>>();
    private Func<T, bool> _matchesAll = (Func<T, bool>) (_ => true);
    private Func<T, bool> _matchesAny = (Func<T, bool>) (_ => true);
    private Func<T, bool> _matchesNone = (Func<T, bool>) (_ => false);

    public void Add(Func<T, bool> filter)
    {
        this._matchesAll = (Func<T, bool>) (x => this._list.All<Func<T, bool>>((Func<Func<T, bool>, bool>) (predicate => predicate(x))));
        this._matchesAny = (Func<T, bool>) (x => this._list.Any<Func<T, bool>>((Func<Func<T, bool>, bool>) (predicate => predicate(x))));
        this._matchesNone = (Func<T, bool>) (x => !this.MatchesAny(x));
        this._list.Add(filter);
    }

    public static CompositePredicate<T> operator +(
        CompositePredicate<T> invokes,
        Func<T, bool> filter)
    {
        invokes.Add(filter);
        return invokes;
    }

    public bool MatchesAll(T target) => this._matchesAll(target);

    public bool MatchesAny(T target) => this._matchesAny(target);

    public bool MatchesNone(T target) => this._matchesNone(target);

    public bool DoesNotMatchAny(T target) => this._list.Count == 0 || !this.MatchesAny(target);
}
    
internal class CompositeFilter<T>
{
    public CompositePredicate<T> Includes { get; set; } = new CompositePredicate<T>();

    public CompositePredicate<T> Excludes { get; set; } = new CompositePredicate<T>();

    public bool Matches(T target) => this.Includes.MatchesAny(target) && this.Excludes.DoesNotMatchAny(target);
}