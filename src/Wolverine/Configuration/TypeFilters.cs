using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.TypeDiscovery;

namespace Wolverine.Configuration;

public interface ITypeFilter
{
    bool Matches(Type type);
    string Description { get; }
}

public class CanCastToFilter : ITypeFilter
{
    private readonly Type _baseType;

    public CanCastToFilter(Type baseType)
    {
        _baseType = baseType;
    }

    public bool Matches(Type type)
    {
        return type.CanBeCastTo(_baseType);
    }

    public string Description => _baseType.IsInterface
        ? $"Implements {_baseType.FullNameInCode()}"
        : $"Inherits from {_baseType.FullNameInCode()}";
}

public class CompositeTypeFilter : ITypeFilter
{
    public List<ITypeFilter> Filters { get; } = new();
    
    /// <summary>
    /// Match types that have the designated attribute
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void WithAttribute<T>() where T : Attribute
    {
        Filters.Add(new HasAttributeFilter<T>());
    }

    /// <summary>
    /// Match types with the given suffix in the type name. This is case sensitive!
    /// </summary>
    /// <param name="suffix"></param>
    public void WithNameSuffix(string suffix)
    {
        Filters.Add(new NameSuffixFilter(suffix));
    }

    /// <summary>
    /// Match types within the given namespace
    /// </summary>
    /// <param name="ns"></param>
    public void InNamespace(string ns)
    {
        Filters.Add(new NamespaceFilter(ns));
    }

    /// <summary>
    /// Match types that implement or inherit from type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void Implements<T>()
    {
        Filters.Add(new CanCastToFilter(typeof(T)));
    }

    /// <summary>
    /// Match types that implement or inherit from the designated type
    /// </summary>
    /// <param name="type"></param>
    public void Implements(Type type)
    {
        Filters.Add(new CanCastToFilter(type));
    }


    public bool Matches(Type type)
    {
        return Filters.Any(x => x.Matches(type));
    }

    public string Description => Filters.Select(x => x.Description).Join(" or ");

    /// <summary>
    /// User defined matching condition
    /// </summary>
    /// <param name="description">Diagnostic description of this condition</param>
    /// <param name="filter"></param>
    public void WithUserDefinedCondition(string description, Func<Type,bool> filter)
    {
        Filters.Add(new LambdaFilter(description, filter));
    }
}

public class TypeQuery
{
    private readonly TypeClassification _classification;
    public CompositeTypeFilter Includes { get; } = new();
    public CompositeTypeFilter Excludes { get; } = new();

    public TypeQuery(TypeClassification classification)
    {
        _classification = classification;
    }

    public TypeQuery(TypeClassification classification, Func<Type, bool> filter) : this(classification)
    {
        Includes.WithUserDefinedCondition("User-defined", filter);
    }

    public IEnumerable<Type> Find(AssemblyTypes assembly)
    {
        return assembly.FindTypes(_classification).Where(type => Includes.Matches(type) && !Excludes.Matches(type));
    }

    public IEnumerable<Type> Find(IEnumerable<Assembly> assemblies)
    {
        return assemblies.Select(TypeRepository.ForAssembly)
            .SelectMany(Find);
    }
}

public class NamespaceFilter : ITypeFilter
{
    private readonly string _ns;

    public NamespaceFilter(string @namespace)
    {
        _ns = @namespace;
    }

    public bool Matches(Type type)
    {
        return type.IsInNamespace(_ns);
    }

    public string Description => $"Is in namespace {_ns}";
}

public class HasAttributeFilter<T> : ITypeFilter where T : Attribute
{
    public bool Matches(Type type)
    {
        return type.HasAttribute<T>();
    }

    public string Description => $"Has attribute {typeof(T).FullNameInCode()}";
}

public class NameSuffixFilter : ITypeFilter
{
    private readonly string _suffix;

    public NameSuffixFilter(string suffix)
    {
        _suffix = suffix;
    }

    public bool Matches(Type type)
    {
        return type.Name.EndsWith(_suffix);
    }

    public string Description => $"Name ends with '{_suffix}'";
}

public class LambdaFilter : ITypeFilter
{
    public Func<Type, bool> Filter { get; }

    public LambdaFilter(string description, Func<Type, bool> filter)
    {
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    public bool Matches(Type type)
    {
        return Filter(type);
    }

    public string Description { get; }
}

// Really only tested in integration with other things
/// <summary>
///     Mechanism to analyze and scan assemblies for exported types
/// </summary>
public static class TypeRepository
{
    private static ImHashMap<Assembly, AssemblyTypes>
        _assemblies = ImHashMap<Assembly, AssemblyTypes>.Empty;

    public static void ClearAll()
    {
        _assemblies = ImHashMap<Assembly, AssemblyTypes>.Empty;
    }

    /// <summary>
    ///     Use to assert that there were no failures in type scanning when trying to find the exported types
    ///     from any Assembly
    /// </summary>
    public static void AssertNoTypeScanningFailures()
    {
        var exceptions =
            FailedAssemblies().Select(x => x.Record.LoadException).Where(x => x != null).ToArray();


        if (exceptions.Any())
        {
            throw new AggregateException(exceptions!);
        }
    }

    /// <summary>
    ///     Query for all assemblies that could not be scanned, usually because
    ///     of missing dependencies
    /// </summary>
    /// <returns></returns>
    public static IReadOnlyList<AssemblyTypes> FailedAssemblies()
    {
        return _assemblies
            .Enumerate()
            .Select(x => x.Value)
            .Where(x => x.Record.LoadException != null)
            .ToArray();
    }

    /// <summary>
    ///     Scan a single assembly
    /// </summary>
    /// <param name="assembly"></param>
    /// <returns></returns>
    public static AssemblyTypes ForAssembly(Assembly assembly)
    {
        if (_assemblies.TryFind(assembly, out var types))
        {
            return types;
        }

        types = new AssemblyTypes(assembly);
        _assemblies = _assemblies.AddOrUpdate(assembly, types);

        return types;
    }

    /// <summary>
    ///     Find types matching a certain criteria from an assembly
    /// </summary>
    /// <param name="assemblies"></param>
    /// <param name="filter"></param>
    /// <returns></returns>
    public static TypeSet FindTypes(IEnumerable<Assembly> assemblies, Func<Type, bool>? filter = null)
    {
        return new TypeSet(assemblies.Select(ForAssembly), filter);
    }


    /// <summary>
    ///     Find types matching a certain criteria and TypeClassification from an Assembly
    /// </summary>
    /// <param name="assemblies"></param>
    /// <param name="classification"></param>
    /// <param name="filter"></param>
    /// <returns></returns>
    public static IEnumerable<Type> FindTypes(IEnumerable<Assembly> assemblies,
        TypeClassification classification, Func<Type, bool>? filter = null)
    {
        var query = new TypeQuery(classification);
        query.Includes.WithUserDefinedCondition("User defined", filter);
        return assemblies.Select(ForAssembly).SelectMany(query.Find);
    }


    public static IEnumerable<Type> FindTypes(Assembly? assembly, TypeClassification classification,
        Func<Type, bool>? filter = null)
    {
        if (assembly == null)
        {
            return Array.Empty<Type>();
        }

        var query = new TypeQuery(classification, filter);
        return query.Find(ForAssembly(assembly));
    }
}