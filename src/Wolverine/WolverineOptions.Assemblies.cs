using System.Diagnostics;
using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    ///     You may use this to "help" Wolverine in testing scenarios to force
    ///     it to consider this assembly as the main application assembly rather
    ///     that assuming that the IDE or test runner assembly is the application assembly
    /// </summary>
    public static Assembly? RememberedApplicationAssembly;

    private Assembly? _applicationAssembly;

    /// <summary>
    ///     The main application assembly for this Wolverine system. You may need or want to explicitly set this in automated
    ///     test harness
    ///     scenarios. Defaults to the application entry assembly
    /// </summary>
    public Assembly? ApplicationAssembly
    {
        get => _applicationAssembly;
        set
        {
            _applicationAssembly = value;
            deriveServiceName();

            if (value != null)
            {
                HandlerGraph.Discovery.Assemblies.Add(value);

                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                if (CodeGeneration != null)
                {
                    CodeGeneration.ApplicationAssembly = value;
                    CodeGeneration.ReferenceAssembly(value);
                }
            }
        }
    }

    /// <summary>
    ///     All of the assemblies that Wolverine is searching for message handlers and
    ///     other Wolverine items
    /// </summary>
    public IEnumerable<Assembly> Assemblies => HandlerGraph.Discovery.Assemblies;


    private Assembly? determineCallingAssembly()
    {
        var stack = new StackTrace();
        var frames = stack.GetFrames();
        var wolverineFrame = frames.LastOrDefault(x =>
            x.HasMethod() && x.GetMethod()?.DeclaringType?.Assembly.GetName().Name == "Wolverine");

        var index = frames.IndexOf(wolverineFrame);
        for (var i = index; i < frames.Length; i++)
        {
            var candidate = frames[i];
            var assembly = candidate.GetMethod()?.DeclaringType?.Assembly;

            if (assembly is null)
            {
                continue;
            }

            if (assembly.HasAttribute<WolverineIgnoreAttribute>())
            {
                continue;
            }

            var assemblyName = assembly.GetName().Name;

            if (assemblyName is null)
            {
                continue;
            }

            if (assemblyName.StartsWith("System") || assemblyName.StartsWith("Microsoft"))
            {
                continue;
            }

            return assembly;
        }

        return Assembly.GetEntryAssembly();
    }

    private void establishApplicationAssembly(string? assemblyName)
    {
        if (assemblyName.IsNotEmpty())
        {
            ApplicationAssembly ??= Assembly.Load(assemblyName);
        }
        else if (RememberedApplicationAssembly != null)
        {
            ApplicationAssembly = RememberedApplicationAssembly;
        }
        else
        {
            RememberedApplicationAssembly = ApplicationAssembly = determineCallingAssembly();
        }

        if (ApplicationAssembly == null)
        {
            throw new InvalidOperationException("Unable to determine an application assembly");
        }

        HandlerGraph.Discovery.Assemblies.Fill(ApplicationAssembly);
    }

    internal void IncludeExtensionAssemblies(Assembly[] assemblies)
    {
        foreach (var assembly in assemblies) HandlerGraph.Discovery.IncludeAssembly(assembly);
    }
}