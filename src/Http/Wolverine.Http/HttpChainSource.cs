using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using JasperFx.Core.TypeScanning;
using Wolverine.Attributes;

namespace Wolverine.Http;

internal class HttpChainSource
{
    private readonly IList<Assembly> _assemblies;

    private readonly ActionMethodFilter _methodFilters = new();
    private readonly CompositeFilter<Type> _typeFilters = new();

    // Opt-in, additive customization supplied by WolverineHttpOptions.CustomizeHttpEndpointDiscovery.
    // Null unless configured; when set, its Excludes drop endpoint types (so HTTP endpoints honour the
    // same namespace splits as HandlerDiscovery filters) and its Includes broaden discovery. GH-3371.
    private readonly TypeQuery? _userDiscovery;
    private readonly bool _hasUserIncludes;

    public HttpChainSource(IEnumerable<Assembly> assemblies, TypeQuery? userDiscovery = null)
    {
        _assemblies = assemblies.ToList();
        _userDiscovery = userDiscovery;
        _hasUserIncludes = userDiscovery is not null && userDiscovery.Includes.Any();

        _typeFilters.Includes += type =>
            type.Name.EndsWith("Endpoint", StringComparison.OrdinalIgnoreCase) ||
            type.Name.EndsWith("Endpoints", StringComparison.OrdinalIgnoreCase);

        _typeFilters.Includes += type => type.GetMethods().Any(m => m.HasAttribute<WolverineHttpMethodAttribute>());
        _typeFilters.Excludes += type => type.HasAttribute<WolverineIgnoreAttribute>();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "Endpoint discovery scan routed through JasperFx TypeQuery — the single annotated scanning entry point (GH-2909). AOT/TypeLoadMode.Static consumers use the pre-generated HttpEndpointRegistry (the FindActions(types) overload) and never reach this scan. See GH-2925.")]
    internal MethodCall[] FindActions()
    {
        // Route the endpoint type scan through JasperFx's central TypeQuery (GH-2909) rather than an
        // ad-hoc Assembly.ExportedTypes walk; the endpoint-specific include/exclude rules stay in
        // _typeFilters, so discovery semantics are unchanged. TypeClassification.All keeps every
        // non-trimmed type (e.g. static endpoint classes) that the previous scan considered.
        var query = new TypeQuery(TypeClassification.All);
        query.Includes.WithCondition("Wolverine HTTP endpoint type", isEndpointType);

        return query.Find(_assemblies)
            .Distinct()
            .SelectMany(actionsFromType).ToArray();
    }

    // The built-in endpoint predicate, plus the opt-in CustomizeHttpEndpointDiscovery filtering. With no
    // user discovery configured (_userDiscovery == null, _hasUserIncludes == false) a type qualifies on
    // the built-in convention alone: public, non-generic, and matched by _typeFilters (name ends in
    // "Endpoint(s)" or carries a [WolverineHttpMethod]).
    private bool isEndpointType(Type x)
    {
        if (!x.IsPublic || x.GetGenericArguments().Length != 0)
        {
            return false;
        }

        // Opt-in exclusions are subtractive: an otherwise-qualifying endpoint type that a user rule matches
        // (e.g. q.Excludes.InNamespace(...)) is dropped, so HTTP endpoints can be split across hosts the
        // same way HandlerDiscovery filters split message handlers.
        if (_userDiscovery is not null && _userDiscovery.Excludes.Matches(x))
        {
            return false;
        }

        if (_typeFilters.Matches(x))
        {
            return true;
        }

        // Opt-in inclusions are additive: they broaden discovery beyond the built-in "*Endpoint(s)" /
        // [WolverineHttpMethod] convention. An included type still only contributes methods that carry a
        // Wolverine HTTP verb attribute (see actionsFromType / _methodFilters).
        return _hasUserIncludes && _userDiscovery!.Includes.Matches(x);
    }

    // Static-mode counterpart to FindActions(): the endpoint types were already discovered and
    // captured into the generated HttpEndpointRegistry at codegen write time, so we apply the normal
    // endpoint-method selection to exactly those types instead of scanning assemblies. See GH-2925.
    internal MethodCall[] FindActions(IReadOnlyList<Type> endpointTypes)
    {
        return endpointTypes
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
    private Func<T, bool> _matchesAll = _ => true;
    private Func<T, bool> _matchesAny = _ => true;
    private Func<T, bool> _matchesNone = _ => false;

    public void Add(Func<T, bool> filter)
    {
        this._matchesAll = (Func<T, bool>) (x => this._list.All(predicate => predicate(x)));
        this._matchesAny = (Func<T, bool>) (x => this._list.Any(predicate => predicate(x)));
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