using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Core.TypeScanning;
using Wolverine.Attributes;

namespace Wolverine;

internal static class ExtensionLoader
{
    private static Assembly[]? _extensions;

    internal static Assembly[] FindExtensionAssemblies()
    {
        if (_extensions != null)
        {
            return _extensions;
        }

        _extensions = AssemblyFinder
            .FindAssemblies(a => a.HasAttribute<WolverineModuleAttribute>(), false)
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
}
