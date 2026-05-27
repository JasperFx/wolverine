using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    private readonly IList<Type> _extensionTypes = new List<Type>();
    internal List<IWolverineExtension> AppliedExtensions { get; } = [];

    /// <summary>
    ///     Discover and apply <see cref="IWolverineExtension" /> types from the compile-time
    ///     manifest emitted by JasperFx.SourceGenerator (the <c>JasperFx.Generated.DiscoveredExtensions</c>
    ///     class in each eligible assembly). This replaces the previous runtime assembly scan
    ///     (ExtensionLoader + the AssemblyFinder.FindAssemblies bin-directory probe). See GH-2902.
    /// </summary>
    internal void DiscoverAndApplyExtensions()
    {
        // The manifest reader only sees assemblies that are already loaded, but a referenced
        // module (e.g. WolverineFx.RuntimeCompilation) may not be loaded yet at bootstrap. Walk
        // the application's reference graph and load the [WolverineModule] assemblies it reaches
        // so "reference the package and it auto-activates" still works — without the old
        // bin-directory glob (Directory.EnumerateFiles) that AssemblyFinder.FindAssemblies used.
        ensureReferencedModuleAssembliesAreLoaded();

        // Include any [WolverineModule]-marked assembly that is now loaded so its handlers
        // participate in discovery, exactly as the old ExtensionLoader.IncludeExtensionAssemblies
        // path did. This also covers pure handler modules declared with a bare
        // [assembly: WolverineModule] that contribute no IWolverineExtension (and so emit no
        // manifest entry).
        var moduleAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.HasAttribute<WolverineModuleAttribute>())
            .ToArray();

        if (moduleAssemblies.Length > 0)
        {
            IncludeExtensionAssemblies(moduleAssemblies);
        }

        // Apply the extensions the JasperFx.SourceGenerator ExtensionDiscoveryGenerator found at
        // compile time. We keep only the types explicitly declared via [WolverineModule<T>] on
        // their own assembly — the exact set the old runtime scan applied. The manifest's
        // marker-interface scan also lists framework-internal IWolverineExtension helpers
        // (DisableExternalTransports, the ConfigureWolverine() lambda extension, …) that are
        // applied explicitly elsewhere and must never be auto-discovered; gating on the declaring
        // assembly's declared [WolverineModule] type excludes them.
        //
        // Applied now — while the IServiceCollection is still mutable — so an extension may still
        // register IoC services (e.g. Module1Extension), matching the historical timing.
        var extensions = GeneratedExtensionManifest.ReadFromLoadedAssemblies()
            .Where(type => type.IsConcrete() && type.CanBeCastTo<IWolverineExtension>())
            .Where(isDeclaredModuleExtension)
            .Select(buildExtension)
            .ToArray();

        ApplyExtensions(extensions);
    }

    // True only when the type is the extension declared by its own assembly's [WolverineModule<T>]
    // (or [WolverineModule(typeof(T))]) attribute. This is what restricts manifest discovery to the
    // same opt-in set the old ExtensionLoader applied, rather than every IWolverineExtension the
    // generator's marker scan happens to list.
    private static bool isDeclaredModuleExtension(Type type)
    {
        return type == type.Assembly.GetAttribute<WolverineModuleAttribute>()?.WolverineExtensionType;
    }

    // Framework assembly name prefixes we never need to walk into. No Wolverine extension/module
    // assembly uses these prefixes, so skipping them keeps the reference-graph walk from loading
    // the whole BCL closure.
    private static readonly string[] _nonModulePrefixes =
        ["System.", "System,", "Microsoft.", "netstandard", "mscorlib", "WindowsBase", "Newtonsoft."];

    // Walk the reference graph from the application assembly plus everything already loaded,
    // loading each non-framework referenced assembly so any [WolverineModule] it carries is present
    // for the manifest read below. This intentionally replaces AssemblyFinder.FindAssemblies'
    // Directory.EnumerateFiles bin-directory probe (which is [RequiresUnreferencedCode] and loads
    // assemblies that may not even be referenced) with a bounded walk over declared references.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Loads assemblies declared in the application's reference graph (Assembly.GetReferencedAssemblies/Assembly.Load), not a filesystem probe. AOT/trim-published apps reference their extension assemblies statically and register extensions explicitly; see GH-2902 / AOT guide.")]
    private void ensureReferencedModuleAssembliesAreLoaded()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>();

        void enqueue(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            if (name != null && seen.Add(name))
            {
                queue.Enqueue(assembly);
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
        {
            enqueue(assembly);
        }

        if (ApplicationAssembly != null)
        {
            enqueue(ApplicationAssembly);
        }

        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                var name = reference.Name;
                if (name == null || seen.Contains(name) || isNonModuleAssembly(name))
                {
                    continue;
                }

                try
                {
                    enqueue(Assembly.Load(reference));
                }
                catch (Exception)
                {
                    // A reference we can't resolve can't be a discoverable module; ignore it.
                    seen.Add(name);
                }
            }
        }
    }

    private static bool isNonModuleAssembly(string name)
    {
        foreach (var prefix in _nonModulePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // The manifest stores extension types as a DAM-less Type[] (typeof(...) literals emitted by the
    // generator), so the IL flow analyzer can't see that constructors are preserved. They are: real
    // extensions are declared via [WolverineModule<T>], whose attribute constructor takes a
    // [DAM(PublicConstructors)] Type, and the generator emits the type with a typeof() literal that
    // survives trimming. AOT-clean apps that trim aggressively register extensions explicitly.
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Extension types come from the source-generated DiscoveredExtensions manifest (typeof literals); public constructors are preserved via the [WolverineModule<T>] attribute's [DAM(PublicConstructors)] Type parameter. See GH-2902 / AOT guide.")]
    private static IWolverineExtension buildExtension(Type type)
    {
        return (IWolverineExtension)Activator.CreateInstance(type)!;
    }


    /// <summary>
    ///     Applies the extension to this application
    /// </summary>
    /// <param name="extension"></param>
    public void Include(IWolverineExtension extension)
    {
        ApplyExtensions([extension]);
    }

    internal void ApplyExtensions(IWolverineExtension[] extensions)
    {
        // Apply idempotency
        extensions = extensions.Where(x => !_extensionTypes.Contains(x.GetType())).ToArray();

        foreach (var extension in extensions)
        {
            try
            {
                extension.Configure(this);
            }
            catch (InvalidOperationException e)
            {
                if (e.Message.Contains("The service collection cannot be modified because it is read-only."))
                {
                    throw new InvalidOperationException(
                        "As of Wolverine 3.0, it's no longer supported to alter IoC service registrations through Wolverine extensions that are themselves registered in the IoC container",
                        e);
                }
                
                throw;
            }
            AppliedExtensions.Add(extension);
        }

        _extensionTypes.Fill(extensions.Select(x => x.GetType()));
    }

    /// <summary>
    ///     Applies the extension with optional configuration to the application
    /// </summary>
    /// <param name="configure">Optional configuration of the extension</param>
    /// <typeparam name="T"></typeparam>
    public void Include<T>(Action<T>? configure = null) where T : IWolverineExtension, new()
    {
        var extension = new T();
        configure?.Invoke(extension);

        ApplyExtensions([extension]);
    }
}