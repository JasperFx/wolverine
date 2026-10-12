using System.Collections.Concurrent;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;

namespace Wolverine.Http.CodeGen;

/// <summary>
///     GH-4978. A hand-written strong-typed identifier like <c>readonly record struct OrderId(Guid Value)</c>: a value
///     type with one public constructor taking exactly one argument, a public <c>Value</c> property of that same
///     type, and no <c>TryParse</c> of its own. Wolverine.Http binds it by parsing the inner value and calling the constructor,
///     so a route, query string, form or claim value can bind straight to it.
/// </summary>
internal sealed class SingleValueWrapper
{
    private static readonly ConcurrentDictionary<Type, SingleValueWrapper?> _wrappers = new();

    private SingleValueWrapper(Type wrapperType, Type innerType)
    {
        WrapperType = wrapperType;
        InnerType = innerType;
    }

    public Type WrapperType { get; }
    public Type InnerType { get; }

    public static bool TryFind(Type type, out SingleValueWrapper wrapper)
    {
        wrapper = _wrappers.GetOrAdd(type, build)!;
        return wrapper != null;
    }

    private static SingleValueWrapper? build(Type type)
    {
        // Value types only, which is how strong-typed ids are conventionally written. A reference type with this
        // same shape is far more often something another frame produces -- a middleware result passed into the
        // endpoint, or JasperFx's own TenantId -- and binding it from the query string as well would take the
        // parameter away from that frame.
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum || type.IsGenericTypeDefinition ||
            type.IsNullable())
        {
            return null;
        }

        // A type with its own TryParse keeps taking that path
        if (type.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(x => x.Name == "TryParse"))
        {
            return null;
        }

        var constructors = type.GetConstructors();
        if (constructors.Length != 1) return null;

        var parameters = constructors[0].GetParameters();
        if (parameters.Length != 1) return null;

        var innerType = parameters[0].ParameterType;

        var valueProperty = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProperty == null || valueProperty.PropertyType != innerType) return null;

        return innerType == typeof(string) || isParsable(innerType)
            ? new SingleValueWrapper(type, innerType)
            : null;
    }

    private static bool isParsable(Type innerType)
    {
        // TryParseSingleValue<TWrapper, TInner> is constrained to IParsable<TInner>, which every primitive, Guid
        // and date type implements. Enums do not, so they are not supported as the inner value.
        return innerType.IsValueType && !innerType.IsEnum &&
               typeof(IParsable<>).MakeGenericType(innerType).IsAssignableFrom(innerType);
    }

    /// <summary>
    ///     C# condition that parses <paramref name="rawValue" /> into <paramref name="outExpression" />, and is false
    ///     for a null or unparseable value
    /// </summary>
    public string TryParseCode(string rawValue, string outExpression)
    {
        var wrapper = WrapperType.FullNameInCode();
        var method = $"{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.TryParseSingleValue)}";

        return InnerType == typeof(string)
            ? $"{method}<{wrapper}>({rawValue}, static inner => new {wrapper}(inner), out {outExpression})"
            : $"{method}<{wrapper}, {InnerType.FullNameInCode()}>({rawValue}, static inner => new {wrapper}(inner), out {outExpression})";
    }

    /// <summary>
    ///     The F# tuple form of <see cref="TryParseCode" /> (F# auto-tuples the out argument)
    /// </summary>
    public string FSharpTryParseCode(string rawValue)
    {
        var wrapper = WrapperType.FSharpName();
        var method = $"{typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.TryParseSingleValue)}";

        return InnerType == typeof(string)
            ? $"{method}<{wrapper}>({rawValue}, System.Func<string, {wrapper}>(fun inner -> {wrapper}(inner)))"
            : $"{method}<{wrapper}, {InnerType.FSharpName()}>({rawValue}, System.Func<{InnerType.FSharpName()}, {wrapper}>(fun inner -> {wrapper}(inner)))";
    }
}

internal static class ParsingCode
{
    /// <summary>
    ///     C# condition that parses <paramref name="rawValue" /> into <paramref name="outExpression" /> for any
    ///     type <see cref="RouteParameterStrategy.CanParse" /> accepts
    /// </summary>
    public static string TryParse(Type type, string rawValue, string outExpression)
    {
        var typeName = type.FullNameInCode();

        if (type.IsEnum)
        {
            // Enum names arrive on the wire as strings and must parse case-insensitively. Without ignoreCase a
            // camelCased value (the default System.Text.Json spelling clients see in responses) is dropped.
            return $"{typeName}.TryParse<{typeName}>({rawValue}, true, out {outExpression})";
        }

        if (type.IsBoolean())
        {
            return $"{typeName}.TryParse({rawValue}, out {outExpression})";
        }

        if (SingleValueWrapper.TryFind(type, out var wrapper))
        {
            return wrapper.TryParseCode(rawValue, outExpression);
        }

        return $"{typeName}.TryParse({rawValue}, System.Globalization.CultureInfo.InvariantCulture, out {outExpression})";
    }
}
