using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using JasperFx.Events.Tags;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     GH-6005 (jasperfx#1297). The consistency tags the derived rung can read off the code: the store's tag
///     catalogue, the tags an event carries, and the command members a Dynamic Consistency Boundary query is
///     built from. Every rule here is a signature or a registration; none reads a method body.
/// </summary>
public static class EventModelTags
{
    /// <summary>
    ///     The model-level tag catalogue (<see cref="EventModelDescriptor.Tags" />): one entry per registered tag
    ///     type, named by <see cref="EventTagDescriptor.NameFor(Type)" /> so a declared <c>Tag&lt;CourseId&gt;()</c>
    ///     and the registry's <c>CourseId</c> merge as one tag.
    /// </summary>
    public static IReadOnlyList<EventTagDescriptor> Catalogue(IEnumerable<ITagTypeRegistration> registrations)
        => registrations
            .Select(x => new EventTagDescriptor(EventTagDescriptor.NameFor(x.TagType), TypeDescriptor.For(x.TagType))
            {
                InnerType = TypeDescriptor.For(x.SimpleType)
            })
            .ToList();

    /// <summary>
    ///     The tags <paramref name="eventType" /> carries: one per registered tag type the event has exactly one
    ///     public readable property of -- the rule <see cref="EventTagInference" /> applies when the store writes
    ///     the event, so the model says what the store will do.
    /// </summary>
    public static IReadOnlyList<string> TagsOf(Type eventType, EventModelDerivation derivation)
        => TagMembersOf(eventType, derivation).Select(x => x.Tag).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    ///     The members of <paramref name="type" /> whose value is a consistency tag, with the tag's name: every
    ///     public readable property whose type is a registered tag type, in declaration order. With no registry
    ///     reachable, the strong-typed identifiers the type carries (the shape <c>scaffold</c> builds a DCB query
    ///     from, GH-4883), so a host described without its store still shows its boundary.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Command and event types come from handler discovery, which already roots them; describe-time diagnostics, never dispatch.")]
    public static IReadOnlyList<(string Member, Type TagType, string Tag)> TagMembersOf(Type type, EventModelDerivation derivation)
    {
        var members = new List<(string Member, Type TagType, string Tag)>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var isTag = derivation.TagTypes.Count > 0
                ? derivation.IsTagType(propertyType)
                : IsStrongTypedId(propertyType);

            if (isTag) members.Add((property.Name, propertyType, EventTagDescriptor.NameFor(propertyType)));
        }

        // EventTagInference tags an event by a tag type only when exactly one member carries it
        return members
            .Where(x => members.Count(other => other.TagType == x.TagType) == 1)
            .ToList();
    }

    /// <summary>
    ///     A strong-typed identifier as a DCB tag reads: a non-primitive, non-generic type wrapping a single
    ///     <c>Value</c> of Guid, string, int or long -- the same rule <c>scaffold</c> applies (GH-4883).
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Member types come from handler discovery, which already roots them; describe-time diagnostics, never dispatch.")]
    public static bool IsStrongTypedId(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type.IsGenericType || type == typeof(string) || type == typeof(Guid)) return false;
        var value = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        return value?.PropertyType is { } inner
               && (inner == typeof(Guid) || inner == typeof(string) || inner == typeof(int) || inner == typeof(long));
    }
}
