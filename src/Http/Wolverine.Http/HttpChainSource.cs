using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;

namespace Wolverine.Http;

internal class HttpChainSource
{
    private readonly IList<Assembly> _assemblies;

    private readonly ActionMethodFilter _methodFilters = new();
    private readonly CompositeFilter<Type> _typeFilters = new();

    public HttpChainSource(IEnumerable<Assembly> assemblies)
    {
        _assemblies = assemblies.ToList();

        _typeFilters.Includes += type =>
            type.Name.EndsWith("Endpoint", StringComparison.OrdinalIgnoreCase) ||
            type.Name.EndsWith("Endpoints", StringComparison.OrdinalIgnoreCase);

        _typeFilters.Includes += type => type.GetMethods().Any(m => m.HasAttribute<WolverineHttpMethodAttribute>());
        _typeFilters.Excludes += type => type.HasAttribute<WolverineIgnoreAttribute>();
    }

    internal MethodCall[] FindActions()
    {
        var discovered = _assemblies.SelectMany(x => x.ExportedTypes)
            .Where(x => _typeFilters.Matches(x))
            .Where(x => x.IsPublic && !x.GetGenericArguments().Any());

        return discovered
            .Distinct()
            .SelectMany(actionsFromType).ToArray();
    }

    private IEnumerable<MethodCall> actionsFromType(Type type)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static)
            .Where(m => _methodFilters.Matches(m))
            .Select(m => new MethodCall(type, m));
    }
}

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

internal class ActionMethodFilter : CompositeFilter<MethodInfo>
{
    public ActionMethodFilter()
    {
        Excludes += method => method.DeclaringType == typeof(object);
        Excludes += method => method.Name == ReflectionHelper.GetMethod<IDisposable>(x => x.Dispose())!.Name;
        Excludes += method => method.ContainsGenericParameters;
        Excludes += method => method.IsSpecialName;
        Excludes += method => method.HasAttribute<WolverineIgnoreAttribute>();

        Includes += method => method.HasAttribute<WolverineHttpMethodAttribute>();
    }

    public void IgnoreMethodsDeclaredBy<T>()
    {
        Excludes += x => x.DeclaringType == typeof(T);
    }
}