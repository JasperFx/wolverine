using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     GH-6005. The facts the derived Event Model sources read beside the handler chains: the event store's
///     registered consistency tag types, whether to reflect the fields of the modelled types, and whether to read
///     XML documentation comments. Everything else a derived slice says still comes off the chain.
/// </summary>
/// <remarks>
///     <para>
///         <b>Tags come from the store's registry, never from a method body.</b> A tag type registered with
///         <c>RegisterTagType&lt;T&gt;()</c> is a fact of the configuration; which tag a <c>Load()</c> method
///         queries by is not read from its body (the 2026-11-20 rule). What <em>is</em> read is the signature: a
///         command member of a registered tag type is the value the query is built from, and an event's member
///         of a registered tag type is the tag the store infers for it (<see cref="EventTagInference" />'s own
///         rule).
///     </para>
///     <para>
///         <b>Fields are reflected at describe time, generated on the AOT path.</b> <see cref="ReflectShapes" />
///         defaults to <see cref="RuntimeFeature.IsDynamicCodeSupported" />: a JIT host, which is what
///         <c>describe-event-model</c>, <c>event-model</c> and <c>scaffold</c> run against, reflects over the
///         record types; a Native AOT host leaves the shapes to the ones <c>JasperFx.SourceGenerator</c> wrote
///         into the event-model manifest, which <see cref="DiscoveredEventModelDefinitions" /> attaches.
///     </para>
/// </remarks>
public sealed class EventModelDerivation
{
    /// <summary>No store registry, reflection where the runtime supports it, documentation comments read.</summary>
    public static EventModelDerivation Default { get; } = new();

    /// <summary>The event store's registered consistency tag types, in registration order. Empty when no store is reachable.</summary>
    public IReadOnlyList<ITagTypeRegistration> TagTypes { get; init; } = Array.Empty<ITagTypeRegistration>();

    /// <summary>
    ///     Reflect the fields of every command, event, read model and aggregate the slices name into
    ///     <c>EventModelDescriptor.Shapes</c> (jasperfx#1299). Off under Native AOT, where the source generator's
    ///     manifest carries them instead.
    /// </summary>
    public bool ReflectShapes { get; init; } = RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    ///     Read the <c>&lt;summary&gt;</c> of each handler, command, event and read model type from the XML
    ///     documentation file beside its assembly into the slice's descriptions (jasperfx#1298).
    /// </summary>
    public bool ReadDocComments { get; init; } = true;

    /// <summary>
    ///     The derivation for a built host: the tag types of every <see cref="IEventStore" /> registered in the
    ///     container, read from the store's configuration without touching a database.
    /// </summary>
    public static EventModelDerivation For(IServiceProvider services)
        => new() { TagTypes = EventTagRegistries.TagTypesIn(services) };

    /// <summary>Is <paramref name="type" /> one of the store's registered tag types?</summary>
    public bool IsTagType(Type type) => TagTypes.Any(x => x.TagType == type);

    /// <summary>The registration of <paramref name="tagType" />, or null when the store knows no such tag.</summary>
    public ITagTypeRegistration? FindTagType(Type tagType) => TagTypes.FirstOrDefault(x => x.TagType == tagType);
}

/// <summary>
///     GH-6005. Reads the consistency tag types the event stores in a container registered, through the
///     <see cref="EventRegistry" /> every Critter Stack store builds its event graph on, so nothing here names a
///     store and nothing here opens a connection.
/// </summary>
public static class EventTagRegistries
{
    /// <summary>
    ///     Every <see cref="ITagTypeRegistration" /> of every <see cref="IEventStore" /> in the container, one per
    ///     tag type. A store whose registry is not an <see cref="EventRegistry" /> contributes none.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Reads IEventStore<,>.Registry off a store type that is rooted by its own registration; describe-time diagnostics, never dispatch.")]
    public static IReadOnlyList<ITagTypeRegistration> TagTypesIn(IServiceProvider services)
    {
        var registrations = new List<ITagTypeRegistration>();

        foreach (var store in services.GetServices<IEventStore>())
        {
            foreach (var registration in TagTypesOf(store))
            {
                if (registrations.All(x => x.TagType != registration.TagType)) registrations.Add(registration);
            }
        }

        return registrations;
    }

    /// <summary>The tag types one store registered, read from its <see cref="EventRegistry" />.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Reads IEventStore<,>.Registry off a store type that is rooted by its own registration; describe-time diagnostics, never dispatch.")]
    public static IReadOnlyList<ITagTypeRegistration> TagTypesOf(IEventStore store)
    {
        try
        {
            // IEventStore<TOperations, TQuerySession>.Registry is the one store-agnostic door to the registry, and
            // the type arguments are the store's own session types, so the closed interface is found by shape
            var closed = store.GetType().GetInterfaces()
                .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEventStore<,>));

            var registry = closed?.GetProperty("Registry", BindingFlags.Public | BindingFlags.Instance)?.GetValue(store);
            return registry is EventRegistry events ? events.TagTypes : Array.Empty<ITagTypeRegistration>();
        }
        catch (Exception)
        {
            // A store that cannot describe itself contributes no tags; a diagnostic never takes the model down
            return Array.Empty<ITagTypeRegistration>();
        }
    }
}
