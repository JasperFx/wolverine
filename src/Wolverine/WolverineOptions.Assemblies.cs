using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

            if (value != null)
            {
                HandlerGraph.Discovery.Assemblies.Fill(value);

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


    // determineCallingAssembly is a best-effort fallback that runs only when the
    // caller didn't pass an assemblyName to UseWolverine, the cached
    // RememberedApplicationAssembly is empty, AND jasperfx.ApplicationAssembly is
    // also empty. AOT-clean apps avoid this path entirely: they explicitly set
    // ApplicationAssembly (or pass assemblyName to UseWolverine) — see the AOT
    // publishing guide. The StackFrame.GetMethod() reflection is only used to
    // identify which calling assembly *registered* Wolverine; the call doesn't
    // need full method metadata, just DeclaringType.Assembly. Trim removal of
    // method bodies still leaves the declaring-type metadata intact for normally-
    // reachable methods, so the practical risk is low even outside the AOT path.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Best-effort caller-assembly resolution; AOT-clean apps set ApplicationAssembly explicitly. See AOT guide.")]
    private Assembly? determineCallingAssembly()
    {
        var stack = new StackTrace();
        var frames = stack.GetFrames();
        var wolverineFrame = frames.LastOrDefault(x =>
            x.HasMethod() && x.GetMethod()?.DeclaringType?.Assembly.GetName().Name == "Wolverine");

        var index = Array.IndexOf(frames, wolverineFrame);

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