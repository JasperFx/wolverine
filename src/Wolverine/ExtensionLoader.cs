using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Core.TypeScanning;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace Wolverine;

internal static class ExtensionLoader
{
    private static Assembly[]? _extensions;
    private static bool hasWarned = false;

    internal static bool IsModule(Assembly assembly)
    {
        try
        {
            if (assembly.HasAttribute<WolverineModuleAttribute>()) return true;
        }
        catch (Exception)
        {
            if (!hasWarned)
            {
                Console.WriteLine("To disable automatic Wolverine extension finding, and stop these messages, see:");
                Console.WriteLine("https://wolverinefx.net/guide/extensions.html#disabling-assembly-scanning");
            }
        }

        return false;
    }

    internal static Assembly[] FindExtensionAssemblies()
    {
        if (_extensions != null)
        {
            return _extensions;
        }

        Action<string> logFailure = msg =>
        {
            if (!hasWarned)
            {
                Console.WriteLine("To disable automatic Wolverine extension finding, and stop these messages, see:");
                Console.WriteLine("https://wolverinefx.net/guide/extensions.html#disabling-assembly-scanning");
            }

            Console.WriteLine(msg);
        };

        _extensions = AssemblyFinder
            .FindAssemblies(logFailure, a =>  a.HasAttribute<WolverineModuleAttribute>(), false)
            .Concat(AppDomain.CurrentDomain.GetAssemblies())
            .Distinct()
            .Where(a => a.HasAttribute<WolverineModuleAttribute>())
            .ToArray();

        var names = _extensions.Select(x => x.GetName().Name);

        Assembly[] FindDependencies(Assembly a)
        {
            return _extensions!.Where(x => names.Contains(x.GetName().Name)).ToArray();
        }

        _extensions = _extensions.TopologicalSort(FindDependencies, false).ToArray();

        return _extensions;
    }

    internal static void ApplyExtensions(WolverineOptions options)
    {
        // Phase E: Check if we have a source-generated type loader with pre-discovered
        // extension types. If so, use those instead of scanning all assemblies.
        var typeLoader = TryFindTypeLoader(options.ApplicationAssembly);
        if (typeLoader?.DiscoveredExtensionTypes?.Count > 0)
        {
            ApplyExtensionsFromTypeLoader(options, typeLoader);
            return;
        }

        // Fallback to runtime assembly scanning
        var assemblies = FindExtensionAssemblies();

        if (assemblies.Length == 0)
        {
            return;
        }

        options.IncludeExtensionAssemblies(assemblies);

        var extensions = assemblies.Select(x => x.GetAttribute<WolverineModuleAttribute>()!.WolverineExtensionType)
            .Where(x => x != null)
            .Select(x => Activator.CreateInstance(x!)!.As<IWolverineExtension>())
            .ToArray();

        options.ApplyExtensions(extensions);
    }

    /// <summary>
    /// Phase E: Apply extensions discovered at compile time by the source generator,
    /// bypassing the expensive AssemblyFinder scanning.
    /// </summary>
    private static void ApplyExtensionsFromTypeLoader(WolverineOptions options, IWolverineTypeLoader typeLoader)
    {
        var extensions = typeLoader.DiscoveredExtensionTypes
            .Where(t => t != null && !t.IsAbstract && typeof(IWolverineExtension).IsAssignableFrom(t))
            .Select(t => (IWolverineExtension)Activator.CreateInstance(t)!)
            .ToArray();

        if (extensions.Length == 0) return;

        // Include the assemblies that contain extension types for handler discovery
        var extensionAssemblies = extensions
            .Select(e => e.GetType().Assembly)
            .Distinct()
            .Where(a => a.HasAttribute<WolverineModuleAttribute>())
            .ToArray();

        if (extensionAssemblies.Length > 0)
        {
            options.IncludeExtensionAssemblies(extensionAssemblies);
        }

        options.ApplyExtensions(extensions);
    }

    /// <summary>
    /// Try to find a source-generated IWolverineTypeLoader from the application assembly
    /// via the [WolverineTypeManifest] assembly attribute.
    /// </summary>
    internal static IWolverineTypeLoader? TryFindTypeLoader(Assembly? applicationAssembly)
    {
        if (applicationAssembly == null) return null;

        var attr = applicationAssembly.GetCustomAttribute<WolverineTypeManifestAttribute>();
        if (attr?.LoaderType == null) return null;

        try
        {
            return (IWolverineTypeLoader)Activator.CreateInstance(attr.LoaderType)!;
        }
        catch
        {
            return null;
        }
    }
}
