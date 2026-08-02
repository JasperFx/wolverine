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

    // GH-3521: a warning buffered during options configuration (before any logger exists) and emitted at
    // WolverineRuntime startup when an implicit host silently inherited a different host's scanned assembly.
    internal string? ApplicationAssemblyReuseWarning { get; private set; }

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

            if (assemblyName.StartsWith("System") || assemblyName.StartsWith("Microsoft") ||
                IsTestRunnerAssembly(assemblyName))
            {
                continue;
            }

            return assembly;
        }

        return Assembly.GetEntryAssembly();
    }

    // GH-3776: a test-runner assembly is never the application assembly. determineCallingAssembly walks the
    // stack for the first frame outside Wolverine that isn't System*/Microsoft*, but under an async test
    // fixture the frames between Wolverine and the test class belong to the RUNNER, not the test assembly —
    // so xUnit v3's own "xunit.v3.core" was adopted for handler discovery. Discovery then scanned an assembly
    // with no handlers in it and every conventionally-discovered handler in the test assembly vanished, with
    // only a downstream IndeterminateRoutesException ("Could not determine any valid subscribers or local
    // handlers") to show for it. Skipping the runner lets the walk continue out to the test assembly; if
    // nothing else matches, the Assembly.GetEntryAssembly() fallback is the test assembly anyway.
    //
    // Prefix matching is deliberate: it covers xunit.v3.core / xunit.execution.dotnet / nunit.framework and
    // the runner hosts, without needing to track exact assembly names across runner versions.
    internal static bool IsTestRunnerAssembly(string assemblyName)
    {
        return assemblyName.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("nunit", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("ReSharperTestRunner", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("JetBrains.", StringComparison.OrdinalIgnoreCase);
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

    // GH-3521: the assembly this host's *own* registration call stack resolves to, captured in the
    // constructor while the caller's frame is still present (determineCallingAssembly is meaningless from
    // the lazy ReadJasperFxOptions path). Compared later against the assembly actually adopted for handler
    // discovery — which, for an implicit host, is the process-pinned jasperfx.ApplicationAssembly — so a
    // first-host-wins mismatch in a multi-host test process becomes a loud warning instead of a silent
    // "No routes can be determined". Null when a caller assembly could not be resolved.
    internal Assembly? RegistrationCallingAssembly { get; private set; }

    internal void CaptureRegistrationCallingAssembly()
    {
        RegistrationCallingAssembly = determineCallingAssembly();
    }

    // GH-3521: record a warning (buffered until a logger exists at runtime startup) when the application
    // assembly adopted for handler discovery differs from where this host was registered. Only meaningful
    // for an implicit host (the user set neither ApplicationAssembly nor an assemblyName) — an explicit
    // choice is always honored silently.
    internal void CheckForDivergentApplicationAssembly(Assembly adopted)
    {
        var registered = RegistrationCallingAssembly;
        if (registered == null)
        {
            return;
        }

        if (string.Equals(registered.GetName().Name, adopted.GetName().Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplicationAssemblyReuseWarning =
            $"Wolverine adopted application assembly '{adopted.GetName().Name}' for handler discovery, but this host was registered from '{registered.GetName().Name}'. " +
            $"The application assembly is a process-wide value pinned by whichever host started FIRST in this process (GH-3521), so handler discovery will NOT scan '{registered.GetName().Name}'. " +
            $"This typically only bites a test harness that stands up multiple Wolverine hosts across different assemblies. If handlers defined in '{registered.GetName().Name}' appear to be missing " +
            $"(e.g. \"No routes can be determined\"), set opts.ApplicationAssembly = typeof(SomeHandler).Assembly or opts.Discovery.IncludeAssembly(...) explicitly on this host.";
    }

    internal void IncludeExtensionAssemblies(Assembly[] assemblies)
    {
        foreach (var assembly in assemblies) HandlerGraph.Discovery.IncludeAssembly(assembly);
    }
}