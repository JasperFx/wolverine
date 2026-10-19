using System.Reflection;
using JasperFx.Events.EventModeling;

namespace Bobcat;

/// <summary>
/// The Event Model half a spec assembly's generated source declares — the slices its projected
/// specifications describe, and the <c>{Feature}/{Scenario}</c>
/// identities they bind to them (issues #106, #170).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reflection, because the generated type is internal to the spec assembly.</b> That is the
/// whole reason the application host cannot see this half — the reference points the other way —
/// and it is why <see cref="Bobcat.Monitoring.SpecEventModelPublisher"/> has to publish it from
/// inside the run. Issue #338 needed the same fact for a second reason (checking the identities
/// against the model), so the lookup moved here rather than being copied: one place knows the
/// generated type's name, and if the emitter ever renames it, one place breaks.
/// </para>
/// <para>
/// <b>Looked up by name rather than by scanning for <c>IEventModelDefinitionSource</c>
/// implementations.</b> Scanning would mean constructing arbitrary user types on the off chance
/// one of them is a model source. The generated type is the one thing here that is ours, and the
/// one thing guaranteed to need no services to describe itself.
/// </para>
/// <para>
/// <b>Failures are the caller's to interpret.</b> A missing generated type is a fact, not an
/// error — an assembly whose specs declare no slices has none — so that is a null. Anything else
/// throws, because the two callers want opposite things from it: a run must never be disturbed by
/// a model it could not read, and an audit that swallowed the same failure would report every
/// declared scenario as uncovered.
/// </para>
/// </remarks>
public static class GeneratedEventModel
{
    /// <summary>
    /// The one type the generator emits per spec assembly — see
    /// <c>EventModelEmitter.EmitSource</c>, which writes this namespace and class name.
    /// </summary>
    public const string GeneratedSourceTypeName = "Bobcat.Generated.EventModel.BobcatEventModelSource";

    /// <summary>
    /// The assembly's generated descriptor, provenance-stamped the way
    /// <c>EventModelDiscovery</c> would stamp it, or null when the assembly has no generated
    /// source at all.
    /// </summary>
    /// <remarks>
    /// A spec assembly's slices are Declared, and the merge upstream decides winning claims by
    /// provenance — so an unstamped half would lose to itself.
    /// </remarks>
    public static EventModelDescriptor? For(Assembly assembly)
    {
        var type = assembly.GetType(GeneratedSourceTypeName, throwOnError: false);
        if (type is null) return null;

        // The generated type and its Instance field are internal to the spec assembly.
        var instance = type
            .GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null) as IEventModelDefinitionSource;

        instance ??= Activator.CreateInstance(type, nonPublic: true) as IEventModelDefinitionSource;
        if (instance is null) return null;

        // The generated implementation ignores the service provider entirely — it is all
        // compile-time fact — and neither caller has a container to offer one.
        // Synchronous by design: both callers are synchronous, and the generated source completes at once.
#pragma warning disable VSTHRD002
        var descriptor = instance
            .TryCreateAsync(EmptyServiceProvider.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        return descriptor?.WithProvenance(instance.Provenance);
    }

    /// <summary>Stands in for the container neither caller has.</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
