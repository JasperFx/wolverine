using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing.Codegen;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Finds the message member that carries one of an aggregate's natural keys, for <c>[WriteModel]</c> and
///     <c>[ReadModel]</c> alike: its owned key, or any key it maps to another projection's lookup. A struct key
///     also matches a <c>TKey?</c> member.
/// </summary>
internal static class NaturalKeyIdentity
{
    /// <param name="findByType">
    ///     The attribute's own identity matching for one type: its explicit argument name, <c>[Identity]</c>,
    ///     the naming conventions and a single member of that type.
    /// </param>
    /// <remarks>
    ///     GH-4439: the chain goes over the seam because natural keys are registered per store. Without it the
    ///     store side can only ask the default store, and an aggregate registered on an ancillary store never
    ///     takes this branch.
    /// </remarks>
    /// <param name="spelling">The attribute as the handler spells it, such as <c>WriteAggregate</c>, for messages</param>
    public static Variable? Find(IEventSourcingFrameProvider provider, Type aggregateType, IChain chain,
        IServiceContainer container, Func<Type, Variable?> findByType, string spelling = "WriteModel")
    {
        var matches = KeyTypesOf(provider, aggregateType, chain, container)
            .Select(keyType => findMember(keyType, findByType))
            .OfType<Variable>()
            .DistinctBy(x => x.Usage)
            .ToArray();

        // Each key of an aggregate has its own type, so a message carrying two of them names two different
        // lookups, and silently taking the first would load by the wrong one.
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"The message for {aggregateType.FullNameInCode()} on {chain.HandlerCalls().First()} carries more than one natural key of the aggregate ({matches.Select(x => x.Usage).Join(", ")}), so which one identifies it is ambiguous. Name the key type to load by, for example {suggest(spelling, "TKey")}.");
        }

        return matches.SingleOrDefault();
    }

    /// <summary>
    ///     The key type a member or value carries: its own type, or <c>TKey</c> for a <c>TKey?</c>.
    /// </summary>
    public static Type KeyTypeOf(Variable naturalKey) => new NaturalKeyArgument(naturalKey).KeyType;

    /// <summary>
    ///     The aggregate's owned key type, if any, then its mapped key types. Never <c>Nullable&lt;T&gt;</c>.
    /// </summary>
    public static IReadOnlyList<Type> KeyTypesOf(IEventSourcingFrameProvider provider, Type aggregateType,
        IChain chain, IServiceContainer container)
    {
        var keyTypes = new List<Type>();
        if (provider.TryDetermineNaturalKeyType(aggregateType, chain, container) is { } owned)
        {
            keyTypes.Add(owned);
        }

        keyTypes.AddRange(provider.DetermineMappedNaturalKeyTypes(aggregateType, chain, container));
        return keyTypes;
    }

    /// <summary>
    ///     The member or value carrying the natural key the attribute names explicitly, with
    ///     <c>NaturalKey = typeof(TKey)</c> or a generic spelling such as <c>[WriteAggregate&lt;TKey&gt;]</c>.
    ///     Only that key type, or <c>TKey?</c> for a struct key, is considered, so there is nothing to be ambiguous
    ///     between, and the stream id lookup is skipped.
    /// </summary>
    /// <param name="explicitName">The name the attribute gives, if any, looked up with <paramref name="explicitSource"/>.</param>
    public static Variable FindByKeyType(IEventSourcingFrameProvider provider, Type aggregateType, Type keyType,
        IChain chain, IServiceContainer container, string? explicitName, ValueSource explicitSource)
    {
        assertIsKeyOf(provider, aggregateType, keyType, chain, container);

        var candidates = memberTypesFor(keyType);

        if (explicitName.IsNotEmpty())
        {
            foreach (var type in candidates)
            {
                if (chain.TryFindVariable(explicitName, explicitSource, type, out var named)) return named;
            }

            throw new InvalidOperationException(
                $"Unable to find a {keyType.FullNameInCode()} named '{explicitName}' to load {aggregateType.FullNameInCode()} by on {chain.HandlerCalls().First()}.");
        }

        if (tryFindIdentityMarked(chain, candidates, out var marked)) return marked;

        foreach (var name in new[] { $"{aggregateType.Name.ToCamelCase()}Id", "id" })
        {
            foreach (var type in candidates)
            {
                if (chain.TryFindVariable(name, ValueSource.Anything, type, out var byConvention))
                {
                    return byConvention;
                }
            }
        }

        if (chain.InputType() is { } inputType)
        {
            var members = keyMembersOf(inputType, candidates).ToArray();
            if (members.Length > 1)
            {
                throw new InvalidOperationException(
                    $"The message for {aggregateType.FullNameInCode()} on {chain.HandlerCalls().First()} has more than one {keyType.FullNameInCode()} member ({members.Select(x => x.Name).Join(", ")}). Name the one to load by in the attribute, or mark it with [Identity].");
            }

            if (members.Length == 1 &&
                chain.TryFindVariable(members[0].Name, ValueSource.InputMember, declaredTypeOf(members[0]), out var single))
            {
                return single;
            }
        }

        throw new InvalidOperationException(
            $"Unable to find the {keyType.FullNameInCode()} natural key to load {aggregateType.FullNameInCode()} by on {chain.HandlerCalls().First()}. Name the member or route value in the attribute, mark it with [Identity], or give the message a single member of that type.");
    }

    /// <summary>
    ///     <c>[Identity]</c> on a member of one of the aggregate's natural key types, when the attribute does not
    ///     name the key type. The stream id lookup would otherwise take the member as the stream id, since
    ///     <c>[Identity]</c> matches a member of any type.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Message types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    public static void AssertIdentityIsNotANaturalKey(IEventSourcingFrameProvider provider, Type aggregateType,
        Type streamIdType, IChain chain, IServiceContainer container, string spelling = "WriteModel")
    {
        var inputType = chain.InputType();
        if (inputType == null) return;

        var marked = inputType.GetMembers().Where(AggregateHandling.IsMarkedAsIdentity).ToArray();
        if (marked.Length == 0) return;

        // A key of the stream id's own type is fetched as a stream id, so it is not one of these
        var keyTypes = KeyTypesOf(provider, aggregateType, chain, container)
            .Where(x => x != streamIdType)
            .ToArray();

        foreach (var member in marked)
        {
            var memberType = member.GetMemberType();
            if (memberType == null) continue;

            var keyType = Nullable.GetUnderlyingType(memberType) ?? memberType;
            if (keyTypes.Contains(keyType))
            {
                throw new InvalidOperationException(
                    $"{inputType.FullNameInCode()}.{member.Name} is marked with [Identity] and is a {keyType.FullNameInCode()}, a natural key of {aggregateType.FullNameInCode()}, on {chain.HandlerCalls().First()}. Name the key type in the attribute, for example {suggest(spelling, keyType.Name)}, so it is loaded by its natural key rather than as a stream id.");
            }
        }
    }

    /// <summary>
    ///     GH-5948. The command member carrying one of <paramref name="keyTypes" />, for the decider workflow, which reads
    ///     the aggregate's identity off the command rather than a parameter attribute. A <c>TKey?</c> member carries a
    ///     struct key too. Null when the command has none.
    /// </summary>
    public static MemberInfo? FindKeyMember(Type aggregateType, Type commandType, IReadOnlyList<Type> keyTypes,
        IChain chain)
    {
        var matches = new List<MemberInfo>();
        foreach (var keyType in keyTypes)
        {
            var members = keyMembersOf(commandType, memberTypesFor(keyType)).ToArray();
            if (members.Length > 1)
            {
                throw new InvalidOperationException(
                    $"{commandType.FullNameInCode()} has more than one {keyType.FullNameInCode()} member ({members.Select(x => x.Name).Join(", ")}), a natural key of {aggregateType.FullNameInCode()}, on {chain.HandlerCalls().First()}. Mark the one to load by with [Identity].");
            }

            matches.AddRange(members);
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"{commandType.FullNameInCode()} carries more than one natural key of {aggregateType.FullNameInCode()} ({matches.Select(x => x.Name).Join(", ")}) on {chain.HandlerCalls().First()}, so which one identifies it is ambiguous. Mark the one to load by with [Identity].");
        }

        return matches.SingleOrDefault();
    }

    /// <summary>
    ///     The member's declared type, <c>TKey?</c> included, which a chain matches a variable by.
    /// </summary>
    public static Type DeclaredTypeOf(MemberInfo member) => declaredTypeOf(member);

    /// <summary>
    ///     How a handler spells an attribute, for messages: <c>WriteAggregate</c> for
    ///     <c>[WriteAggregate&lt;OrderNumber&gt;]</c>, <c>Aggregate</c> for the HTTP <c>[Aggregate]</c>.
    /// </summary>
    public static string SpellingOf(Attribute attribute)
    {
        var name = attribute.GetType().Name;
        var tick = name.IndexOf('`');
        if (tick > 0) name = name[..tick];
        return name.EndsWith("Attribute") ? name[..^"Attribute".Length] : name;
    }

    /// <summary>
    ///     GH-5947 (E2): the attribute the handler already uses with the key type named, and <c>[WriteModel&lt;TKey&gt;]</c>
    ///     or <c>[ReadModel&lt;TKey&gt;]</c> beside it, which every store's integration has.
    /// </summary>
    private static string suggest(string spelling, string keyType, string? name = null)
    {
        var arguments = name == null ? "" : $"(\"{name}\")";
        var core = spelling.StartsWith("Read") ? "ReadModel" : "WriteModel";
        return spelling == core
            ? $"[{spelling}<{keyType}>{arguments}]"
            : $"[{spelling}<{keyType}>{arguments}] or [{core}<{keyType}>{arguments}]";
    }

    /// <summary>
    ///     GH-5947 (U4): <c>[WriteAggregate&lt;OrderNumber&gt;(NaturalKey = typeof(CustomerCode))]</c> names two keys,
    ///     and used to load by the second without a word. The same type twice is harmless.
    /// </summary>
    public static void AssertOneKeyType(Type? typeArgument, Type? naturalKey, string spelling, Type aggregateType,
        IChain chain)
    {
        if (typeArgument == null || naturalKey == null || typeArgument == naturalKey) return;

        throw new InvalidOperationException(
            $"[{spelling}<{typeArgument.NameInCode()}>(NaturalKey = typeof({naturalKey.NameInCode()}))] for {aggregateType.FullNameInCode()} on {chain.HandlerCalls().First()} names two different natural key types. Name the key to load by once, as the type argument or as NaturalKey.");
    }

    /// <summary>
    ///     GH-5947 (E1): what to add to "Unable to determine an aggregate id" when the aggregate has natural keys, which
    ///     the plain message never mentioned. Empty when it has none.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Message types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    public static string ExplainMissingIdentity(IEventSourcingFrameProvider provider, Type aggregateType,
        Type streamIdType, IChain chain, IServiceContainer container, string spelling)
    {
        var keyTypes = KeyTypesOf(provider, aggregateType, chain, container);
        if (keyTypes.Count == 0) return string.Empty;

        var keys = keyTypes.Select(x => x.NameInCode()).Join(", ");
        var explanation =
            $" {aggregateType.NameInCode()} is loaded by its stream id ({streamIdType.NameInCode()}) or by its natural key{(keyTypes.Count > 1 ? "s" : "")} {keys}.";

        var inputType = chain.InputType();
        if (inputType == null)
        {
            return explanation +
                   $" Name the route or query string value to load by, as in {suggest(spelling, keyTypes[0].Name, "number")}.";
        }

        foreach (var keyType in keyTypes)
        {
            var members = keyMembersOf(inputType, memberTypesFor(keyType)).Select(x => x.Name).ToArray();
            if (members.Length > 1)
            {
                return explanation +
                       $" {inputType.NameInCode()} has more than one {keyType.NameInCode()} member ({members.Join(", ")}): name the one to load by, as in {suggest(spelling, keyType.Name, members[0])}.";
            }
        }

        return explanation +
               $" {inputType.NameInCode()} has no member of {(keyTypes.Count > 1 ? "any of those types" : "that type")}, and a member of another type, such as a string, isn't read as one. Give it a {keyTypes[0].NameInCode()} member, or name the member or value to load by, as in {suggest(spelling, keyTypes[0].Name, "Name")}.";
    }

    /// <summary>
    ///     GH-5947 (U2). An attribute naming a route value with no key type reads it as the stream id, so an endpoint for
    ///     an aggregate with natural keys answers 404 to a request carrying the key. Logged rather than refused at code
    ///     generation: a stream id route value named after the key is legal.
    /// </summary>
    public static void WarnOfUntypedRouteValue(IEventSourcingFrameProvider provider, Type aggregateType,
        string? routeValueName, IChain chain, IServiceContainer container, string spelling)
    {
        if (UntypedRouteValueWarning(provider, aggregateType, routeValueName, chain, container, spelling) is not
            { } warning) return;

        if (container.Services.GetService(typeof(ILoggerFactory)) is not ILoggerFactory loggers) return;

        loggers.CreateLogger(typeof(NaturalKeyIdentity)).LogWarning("{Warning}", warning);
    }

    internal static string? UntypedRouteValueWarning(IEventSourcingFrameProvider provider, Type aggregateType,
        string? routeValueName, IChain chain, IServiceContainer container, string spelling)
    {
        if (routeValueName.IsEmpty() || chain is not IRoutedChain routed) return null;
        if (!routed.RouteParameterNames.Any(x => x.EqualsIgnoreCase(routeValueName!))) return null;

        var keyTypes = KeyTypesOf(provider, aggregateType, chain, container);
        if (keyTypes.Count == 0) return null;

        return
            $"[{spelling}(\"{routeValueName}\")] on {chain.HandlerCalls().First()} reads the route value '{routeValueName}' as the stream id of {aggregateType.FullNameInCode()}, which also has the natural key{(keyTypes.Count > 1 ? "s" : "")} {keyTypes.Select(x => x.NameInCode()).Join(", ")}. If the route carries a natural key, name its type, as in {suggest(spelling, keyTypes[0].Name, routeValueName)}: read as a stream id, a natural key finds nothing and the endpoint answers 404.";
    }

    private static void assertIsKeyOf(IEventSourcingFrameProvider provider, Type aggregateType, Type keyType,
        IChain chain, IServiceContainer container)
    {
        if (Nullable.GetUnderlyingType(keyType) is { } underlying)
        {
            throw new InvalidOperationException(
                $"The natural key type for {aggregateType.FullNameInCode()} on {chain.HandlerCalls().First()} is {keyType.FullNameInCode()}. Name {underlying.FullNameInCode()} instead: a {keyType.FullNameInCode()} member is still matched to it.");
        }

        var keyTypes = KeyTypesOf(provider, aggregateType, chain, container);
        if (!keyTypes.Contains(keyType))
        {
            var known = keyTypes.Count == 0
                ? "it has no natural keys"
                : $"its natural keys are {keyTypes.Select(x => x.FullNameInCode()).Join(", ")}";

            throw new InvalidOperationException(
                $"{aggregateType.FullNameInCode()} has no natural key of type {keyType.FullNameInCode()} ({known}), so it cannot be loaded by one on {chain.HandlerCalls().First()}.");
        }
    }

    private static bool tryFindIdentityMarked(IChain chain, Type[] candidates, [NotNullWhen(true)] out Variable? variable)
    {
        variable = null;
        if (chain.InputType() is not { } inputType) return false;

        foreach (var member in keyMembersOf(inputType, candidates).Where(AggregateHandling.IsMarkedAsIdentity))
        {
            if (chain.TryFindVariable(member.Name, ValueSource.InputMember, declaredTypeOf(member), out variable))
            {
                return true;
            }
        }

        return false;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Message types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    private static IEnumerable<MemberInfo> keyMembersOf(Type inputType, Type[] candidates)
    {
        foreach (var property in inputType.GetProperties().Where(x => x.CanRead && candidates.Contains(x.PropertyType)))
        {
            yield return property;
        }

        foreach (var field in inputType.GetFields().Where(x => candidates.Contains(x.FieldType)))
        {
            yield return field;
        }
    }

    // GetMemberType() strips Nullable<T>, and a chain matches a member by its exact declared type
    private static Type declaredTypeOf(MemberInfo member)
        => member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;

    private static Type[] memberTypesFor(Type keyType)
        => keyType.IsValueType ? [keyType, nullableOf(keyType)] : [keyType];

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes Nullable<T> over a natural key struct at codegen time only, which an AOT-published app pre-generates. See docs/guide/aot.md.")]
    private static Type nullableOf(Type keyType) => typeof(Nullable<>).MakeGenericType(keyType);

    private static Variable? findMember(Type keyType, Func<Type, Variable?> findByType)
    {
        if (findByType(keyType) is { } exact) return exact;

        // The key is fetched as TKey after a null check, so a TKey? member carries it too
        return keyType.IsValueType && Nullable.GetUnderlyingType(keyType) == null
            ? findByType(nullableOf(keyType))
            : null;
    }
}
