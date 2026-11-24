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
        var owned = provider.TryDetermineNaturalKeyType(aggregateType, chain, container);
        var found = KeyTypesOf(provider, aggregateType, chain, container)
            .Select(keyType => (KeyType: keyType,
                Variable: findMember(keyType, findByType) ?? findMemberNamedLikeTheKey(aggregateType, keyType, chain)))
            .Where(x => x.Variable != null)
            .DistinctBy(x => x.Variable!.Usage)
            .ToArray();

        // GH-5954: the aggregate's own key ahead of a key it maps from another projection. Both lookups lead to the
        // same stream for this aggregate, so taking the owned one is no guess
        if (found.Length > 1 && owned != null && found.FirstOrDefault(x => x.KeyType == owned) is { Variable: { } ownedKey })
        {
            return ownedKey;
        }

        var matches = found.Select(x => x.Variable!).ToArray();

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

            // GH-5954: of two members of the key type, the one named like the aggregate's key property
            if (members.Length > 1 && namedLikeTheKey(aggregateType, keyType, members) is { } namedMember &&
                chain.TryFindVariable(namedMember.Name, ValueSource.InputMember, declaredTypeOf(namedMember),
                    out var named))
            {
                return named;
            }

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
    /// <param name="ownedKeyType">The aggregate's own key type, preferred over a mapped key. GH-5954</param>
    public static MemberInfo? FindKeyMember(Type aggregateType, Type commandType, IReadOnlyList<Type> keyTypes,
        IChain chain, Type? ownedKeyType = null)
    {
        var matches = new List<MemberInfo>();
        foreach (var keyType in keyTypes)
        {
            var members = keyMembersOf(commandType, memberTypesFor(keyType)).ToArray();

            // GH-5954: of two members of the key type, the one named like the aggregate's key property
            if (members.Length > 1 && namedLikeTheKey(aggregateType, keyType, members) is { } namedMember)
            {
                members = [namedMember];
            }

            if (members.Length > 1)
            {
                throw new InvalidOperationException(
                    $"{commandType.FullNameInCode()} has more than one {keyType.FullNameInCode()} member ({members.Select(x => x.Name).Join(", ")}), a natural key of {aggregateType.FullNameInCode()}, on {chain.HandlerCalls().First()}. Mark the one to load by with [Identity].");
            }

            matches.AddRange(members);
        }

        // GH-5954: the aggregate's own key ahead of a key it maps from another projection
        if (matches.Count > 1 && ownedKeyType != null &&
            matches.FirstOrDefault(x => memberTypesFor(ownedKeyType).Contains(declaredTypeOf(x))) is { } ownedMember)
        {
            return ownedMember;
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
    ///     GH-5954. The route value carrying one of the aggregate's natural keys, when nothing names the key type. Tried
    ///     ahead of the stream id, which any route value parses into, in this order for each route value (the one the
    ///     attribute names, or every one in the route):
    ///     <list type="number">
    ///         <item>an endpoint parameter of that name decides by its type: the key, or not;</item>
    ///         <item>a <c>guid</c> route constraint on a Guid stream id makes it the stream id;</item>
    ///         <item>a name matching the aggregate's key member (<c>Code</c> for <c>{code}</c>) or the key type
    ///         (<c>{gadgetCode}</c>) makes it that key;</item>
    ///         <item>anything else, <c>{id}</c> and <c>{orderId}</c> included, is left to the stream id lookup.</item>
    ///     </list>
    ///     Null when no route value is a key, and for a chain with no route.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Aggregate types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    public static Variable? FindRouteNaturalKey(IEventSourcingFrameProvider provider, Type aggregateType,
        Type streamIdType, string? routeValueName, IChain chain, IServiceContainer container)
    {
        if (chain is not IRoutedChain routed) return null;

        var keyTypes = KeyTypesOf(provider, aggregateType, chain, container).Where(x => x != streamIdType).ToArray();
        if (keyTypes.Length == 0) return null;

        var names = routed.RouteParameterNames
            .Where(x => routeValueName.IsEmpty() || x.EqualsIgnoreCase(routeValueName!))
            .ToArray();

        var found = names
            .Select(name => (Name: name, KeyType: routeValueKeyType(aggregateType, streamIdType, keyTypes, name, chain, routed)))
            .Where(x => x.KeyType != null)
            .ToArray();

        if (found.Length > 1)
        {
            throw new InvalidOperationException(
                $"The route for {aggregateType.FullNameInCode()} on {chain.HandlerCalls().First()} carries more than one of its natural keys ({found.Select(x => $"{{{x.Name}}}").Join(", ")}). Name the route value to load by in the attribute.");
        }

        if (found.Length == 0) return null;

        var (routeName, keyType) = found[0];
        return chain.TryFindVariable(routeName, ValueSource.Anything, keyType!, out var variable) ? variable : null;
    }

    private static Type? routeValueKeyType(Type aggregateType, Type streamIdType, Type[] keyTypes, string name,
        IChain chain, IRoutedChain routed)
    {
        // 1. Declared by the endpoint: its type decides
        var declared = chain.HandlerCalls()
            .SelectMany(x => x.Method.GetParameters())
            .FirstOrDefault(x => x.Name != null && x.Name.EqualsIgnoreCase(name));
        if (declared != null)
        {
            var declaredType = Nullable.GetUnderlyingType(declared.ParameterType) ?? declared.ParameterType;
            return keyTypes.FirstOrDefault(x => x == declaredType);
        }

        // 2. {id:guid} on a Guid stream id
        var streamIdInner = StrongTypedIdResolver.TryResolve(streamIdType, out var info, StrongTypedIdStyle.AnyType)
            ? info.ValueProperty.PropertyType
            : streamIdType;
        if (streamIdInner == typeof(Guid) &&
            routed.RouteParameterConstraints(name).Any(x => x.EqualsIgnoreCase("guid")))
        {
            return null;
        }

        // 3. Named like the key
        return keyTypes.FirstOrDefault(keyType =>
            keyType.Name.EqualsIgnoreCase(name) || keyMemberNamesOf(aggregateType, keyType).Any(x => x.EqualsIgnoreCase(name)));
    }

    /// <summary>
    ///     The aggregate's own members of a key type, such as <c>Code</c> for a <c>GadgetCode</c> key: what a route value
    ///     or a message member carrying that key is most likely named after.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Aggregate types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    private static string[] keyMemberNamesOf(Type aggregateType, Type keyType)
    {
        var candidates = memberTypesFor(keyType);
        return keyMembersOf(aggregateType, candidates).Select(x => x.Name).ToArray();
    }

    private static MemberInfo? namedLikeTheKey(Type aggregateType, Type keyType, MemberInfo[] members)
    {
        var keyNames = keyMemberNamesOf(aggregateType, keyType);
        var named = members.Where(m => keyNames.Any(k => k.EqualsIgnoreCase(m.Name))).ToArray();
        return named.Length == 1 ? named[0] : null;
    }

    // A message member of the key type named like the aggregate's key member, when there are several of that type
    private static Variable? findMemberNamedLikeTheKey(Type aggregateType, Type keyType, IChain chain)
    {
        if (chain.InputType() is not { } inputType) return null;

        var members = keyMembersOf(inputType, memberTypesFor(keyType)).ToArray();
        if (members.Length < 2 || namedLikeTheKey(aggregateType, keyType, members) is not { } member) return null;

        return chain.TryFindVariable(member.Name, ValueSource.InputMember, declaredTypeOf(member), out var variable)
            ? variable
            : null;
    }

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
    ///     generation: a stream id route value named after the key is legal. Quiet for a route value named like a stream
    ///     id (<c>id</c>, <c>orderId</c>), typed by a route constraint, or declared as a parameter of the endpoint.
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

        // Named like a stream id, as in {id} or {orderId}, or declared by the endpoint with its own type: the stream id
        // is what's meant, and a warning on every such endpoint would only be noise
        if (routeValueName!.EndsWith("id", StringComparison.OrdinalIgnoreCase)) return null;
        if (routed.RouteParameterConstraints(routeValueName).Count > 0) return null;
        if (chain.HandlerCalls().Any(call =>
                call.Method.GetParameters().Any(x => routeValueName.EqualsIgnoreCase(x.Name ?? string.Empty)))) return null;

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
