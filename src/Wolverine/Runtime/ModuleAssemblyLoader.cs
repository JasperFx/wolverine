using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core;

namespace Wolverine.Runtime;

/// <summary>
///     Loads the deployed module assemblies — those declaring a <c>[JasperFxAssembly]</c>-derived marker
///     such as <c>[WolverineModule]</c> or <c>[WolverineHandlerModule]</c> — so their attributes and
///     source-generated manifests can be read without an <c>AssemblyFinder</c> filesystem probe. Shared by
///     extension discovery (GH-2902) and handler-module discovery (GH-2905).
/// </summary>
internal static class ModuleAssemblyLoader
{
    // Framework assembly name prefixes we never need to walk into. No Wolverine extension/module
    // assembly uses these prefixes, so skipping them keeps the reference-graph walk from loading
    // the whole BCL closure.
    private static readonly string[] _nonModulePrefixes =
        ["System.", "System,", "Microsoft.", "netstandard", "mscorlib", "WindowsBase", "Newtonsoft."];

    // Make sure every deployed module assembly is loaded so its attributes / source-generated manifest
    // can be inspected. This intentionally replaces AssemblyFinder.FindAssemblies'
    // Directory.EnumerateFiles bin-directory probe.
    //
    // It does NOT just walk Assembly.GetReferencedAssemblies(): the C# compiler prunes a referenced
    // assembly whose types the application never uses directly, so a "reference the package and it
    // auto-activates" module like WolverineFx.RuntimeCompilation is absent from the metadata reference
    // graph even though it is deployed. Instead we use the runtime's resolved deployment list
    // (TRUSTED_PLATFORM_ASSEMBLIES) - not a recursive filesystem scan - which DOES include those
    // pruned-but-deployed assemblies. The reference-graph walk is kept as a fallback for hosts that
    // don't surface that list (e.g. single-file deployments).
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Loads assemblies from the runtime's resolved deployment list / declared reference graph (Assembly.Load), not a filesystem probe. AOT/trim-published apps reference their module assemblies statically and register explicitly (ExtensionDiscovery.ManualOnly); see GH-2902 / GH-2905 / AOT guide.")]
    public static void EnsureDeployedModuleAssembliesAreLoaded(Assembly? applicationAssembly)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
        {
            var name = assembly.GetName().Name;
            if (name != null)
            {
                seen.Add(name);
            }
        }

        if (!tryLoadFromDeploymentList(seen))
        {
            walkReferenceGraph(seen, applicationAssembly);
        }
    }

    // Load the non-framework assemblies the runtime resolved for this app (the deployment closure),
    // which includes referenced module packages whose types the app doesn't use directly. Returns
    // false when the host doesn't expose TRUSTED_PLATFORM_ASSEMBLIES so the caller can fall back.
    private static bool tryLoadFromDeploymentList(HashSet<string> seen)
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string list || list.Length == 0)
        {
            return false;
        }

        foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.IsEmpty() || isNonModuleAssembly(name) || !seen.Add(name))
            {
                continue;
            }

            try
            {
                Assembly.Load(new AssemblyName(name));
            }
            catch (Exception)
            {
                // Not loadable by simple name (native image, resource/satellite assembly, …); skip.
            }
        }

        return true;
    }

    // Fallback: walk the reference graph from the application assembly plus everything already loaded,
    // loading each non-framework referenced assembly. Misses compiler-pruned references, but better
    // than nothing when no deployment list is available.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reference-graph fallback (Assembly.GetReferencedAssemblies/Assembly.Load) used only when the runtime deployment list is unavailable. AOT/trim-published apps register modules explicitly (ExtensionDiscovery.ManualOnly); see GH-2902 / GH-2905 / AOT guide.")]
    private static void walkReferenceGraph(HashSet<string> seen, Assembly? applicationAssembly)
    {
        var queue = new Queue<Assembly>();

        void enqueue(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            if (name != null && seen.Add(name))
            {
                queue.Enqueue(assembly);
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToArray())
        {
            // Already in `seen` from the caller; enqueue for walking without re-adding.
            queue.Enqueue(assembly);
        }

        if (applicationAssembly != null)
        {
            enqueue(applicationAssembly);
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
}
