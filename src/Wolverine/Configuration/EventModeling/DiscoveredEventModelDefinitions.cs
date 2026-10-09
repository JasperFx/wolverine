using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     GH-4916. Every <see cref="EventModelDefinition" /> subclass in the application's assemblies, registered
///     with no <c>AddEventModel&lt;T&gt;()</c> per definition, from the compile-time manifest
///     JasperFx.SourceGenerator writes into each assembly (jasperfx#993). One definition per chapter is the
///     default the import writes (bobcat#448), and a forgotten registration would silently drop a chapter
///     out of the model.
/// </summary>
/// <remarks>
///     <para>
///         <b>Manifest only, never a scan.</b> An assembly the generator did not run on contributes nothing,
///         which is logged at debug level; it is not enumerated by reflection. WolverineFx flows
///         JasperFx.SourceGenerator to every consumer, so an application assembly has the manifest.
///     </para>
///     <para>
///         <b>Lazy.</b> The assemblies are read when the model is assembled, not when services are registered,
///         because <see cref="WolverineOptions.ApplicationAssembly" /> may still change until then. A
///         definition already registered with <c>AddEventModel&lt;T&gt;()</c> is skipped, so the two compose.
///     </para>
///     <para>
///         Definitions with no <see cref="EventModelDefinition.Name" /> all join the application's model
///         (jasperfx#992), so this one source returns them merged into it. One that names a separate model
///         must be registered explicitly with <c>AddEventModel&lt;T&gt;()</c>; it is reported, not merged.
///     </para>
/// </remarks>
public sealed class DiscoveredEventModelDefinitions : IEventModelDefinitionSource
{
    public const string ManifestTypeName = "JasperFx.Generated.DiscoveredEventModels";

    public static readonly Uri SourceSubject = new("event-model://discovered-definitions");

    public Uri Subject => SourceSubject;

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "The definition types come from the generated DiscoveredEventModels manifest, which roots each one's public constructors with [DynamicDependency].")]
    public async Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
    {
        var options = services.GetService<WolverineOptions>();
        if (options is null || !options.AutoRegisterEventModelDefinitions) return null;

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<DiscoveredEventModelDefinitions>();

        // Already registered by hand: EventModelDefinitionSource names its subject for the definition class
        var registered = services.GetServices<IEventModelDefinitionSource>()
            .Where(x => !ReferenceEquals(x, this))
            .Select(x => x.Subject)
            .ToHashSet();

        var definitions = new List<EventModelDescriptor>();
        foreach (var type in DefinitionTypes(options, logger))
        {
            var source = EventModelDefinitionSource.For(type);
            if (registered.Contains(source.Subject)) continue;

            var descriptor = await source.TryCreateAsync(services, token).ConfigureAwait(false);
            if (descriptor is not null) definitions.Add(descriptor);
        }

        if (definitions.Count == 0) return null;

        var application = EventModelDiscovery.ApplicationModelName(services);
        var merged = EventModelDescriptor.GroupByName(definitions);
        foreach (var other in merged.Where(x => x.Name != application))
        {
            logger?.LogWarning(
                "Event model definition(s) for the separately named model '{Model}' were discovered but not registered. Register them with services.AddEventModel<T>().",
                other.Name);
        }

        return merged.FirstOrDefault(x => x.Name == application);
    }

    /// <summary>The definition types every assembly Wolverine scans lists in its generated manifest.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reads the generated JasperFx.Generated.DiscoveredEventModels type by name; when trimmed away the lookup degrades to 'no manifest'.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "DefinitionTypes is a generated public static property of the manifest type, which roots each listed constructor with [DynamicDependency].")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Each listed type's public constructors are rooted by the manifest's [DynamicDependency].")]
    public static IReadOnlyList<Type> DefinitionTypes(WolverineOptions options, ILogger? logger = null)
    {
        var types = new List<Type>();
        foreach (var assembly in new[] { options.ApplicationAssembly }.Concat(options.Assemblies).OfType<Assembly>().Distinct())
        {
            var manifest = assembly.GetType(ManifestTypeName);
            if (manifest?.GetProperty("DefinitionTypes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) is not IEnumerable<Type> listed)
            {
                logger?.LogDebug("{Assembly} has no source-generated event model definition manifest, so no definitions are discovered in it",
                    assembly.GetName().Name);
                continue;
            }

            types.AddRange(listed.Where(x => !types.Contains(x)));
        }

        return types;
    }
}
