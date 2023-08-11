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