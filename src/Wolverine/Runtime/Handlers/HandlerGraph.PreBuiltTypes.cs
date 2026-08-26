using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using JasperFx.CodeGeneration;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph
{
    /// <summary>
    ///     GH-4151. In <see cref="TypeLoadMode.Static" /> there is no fallback: a handler chain whose
    ///     pre-generated type is not in the application assembly can never be executed. Until now the miss
    ///     was only discovered lazily, on the first message of that type, from inside
    ///     <see cref="HandlerFor(Type)" /> while the executor was being built -- too late for any failure
    ///     policy and too late for a deploy to be rolled back. Attach every expected type up front instead,
    ///     so the misconfiguration is a failed start rather than a per-message loss. Attaching is not merely
    ///     a probe: the types the loader would otherwise resolve one message type at a time are resolved
    ///     here, which is what Static mode wanted in the first place.
    /// </summary>
    internal void AssertPreBuiltTypesExist(WolverineOptions options)
    {
        if (options.CodeGeneration.TypeLoadMode != TypeLoadMode.Static)
        {
            return;
        }

        // `codegen write` runs against an application whose types do not exist yet -- that is the point of
        // running it. Same guard as shouldConsumeStaticRegistry.
        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            return;
        }

        var applicationAssembly = options.CodeGeneration.ApplicationAssembly;
        if (applicationAssembly == null)
        {
            return;
        }

        var collection = (ICodeFileCollection)this;
        var containingNamespace = collection.ToNamespace(options.CodeGeneration);

        var missing = new List<ICodeFile>();
        foreach (var file in collection.BuildFiles())
        {
            // The pre-generated HandlerRegistry is deliberately not fatal. Its absence only costs the
            // cold-start optimization -- compileWithRuntimeScanning already warns and falls back to an
            // assembly scan -- and no message is lost over it.
            if (file is HandlerRegistryCodeFile)
            {
                continue;
            }

            if (!file.AttachTypesSynchronously(options.CodeGeneration, applicationAssembly, Container.Services,
                    containingNamespace))
            {
                missing.Add(file);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        throw new MissingPreBuiltTypesException(describeMissingTypes(options, applicationAssembly, missing));
    }

    private static string describeMissingTypes(WolverineOptions options, Assembly applicationAssembly,
        List<ICodeFile> missing)
    {
        var message = new StringBuilder();

        message.AppendLine(
            $"Wolverine is running in {nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Static)}, but {missing.Count} expected pre-built handler type(s) could not be loaded from the configured {nameof(WolverineOptions.ApplicationAssembly)} '{applicationAssembly.GetName().Name}':");

        foreach (var file in missing)
        {
            message.AppendLine("  * " + file);
        }

        message.AppendLine();

        var elsewhere = findAssemblyHoldingGeneratedTypes(options, applicationAssembly);
        if (elsewhere != null)
        {
            message.AppendLine(
                $"Pre-generated Wolverine types were found in '{elsewhere.GetName().Name}' instead. 'dotnet run -- codegen write' emits its source into the entry project, while {nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Static)} loads pre-built types from {nameof(WolverineOptions)}.{nameof(WolverineOptions.ApplicationAssembly)}, so the two disagree.");
            message.AppendLine(
                $"If {nameof(WolverineOptions.ApplicationAssembly)} was only set so that Wolverine would discover handlers living in another assembly, use opts.Discovery.{nameof(Configuration.HandlerDiscovery.IncludeAssembly)}(...) for that instead and leave {nameof(WolverineOptions.ApplicationAssembly)} as the entry assembly. Otherwise, point the generated code output at the project that builds '{applicationAssembly.GetName().Name}' with opts.CodeGeneration.{nameof(GenerationRules.GeneratedCodeOutputPath)}.");
        }
        else
        {
            message.AppendLine(
                "No pre-generated Wolverine types could be found in any assembly this application is using. Run 'dotnet run -- codegen write' as part of the build and compile its output into the application assembly, or run in TypeLoadMode.Auto.");
        }

        message.AppendLine();
        message.Append("See https://wolverinefx.net/guide/codegen.html");

        return message.ToString();
    }

    // Purely a diagnostic for the exception message above: say where the generated code actually landed,
    // because "the types are missing" and "the types are in the other assembly" call for different fixes.
    // The generated HandlerRegistry is the marker -- codegen write always emits exactly one, alongside the
    // handler types, into whichever project it wrote to.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "ExportedTypes walk over candidate assemblies to locate misplaced codegen output; runs only on the startup failure path while building an exception message. See AOT guide.")]
    private static Assembly? findAssemblyHoldingGeneratedTypes(WolverineOptions options, Assembly applicationAssembly)
    {
        var candidates = new List<Assembly>();

        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly != null)
        {
            candidates.Add(entryAssembly);
        }

        candidates.AddRange(options.Assemblies);

        foreach (var candidate in candidates.Distinct().Where(x => x != applicationAssembly && !x.IsDynamic))
        {
            try
            {
                if (candidate.ExportedTypes.Any(x => x.Name == HandlerRegistry.GeneratedTypeName))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // A candidate assembly that cannot be reflected over simply is not a candidate. This probe
                // exists to make an exception message more useful and must never become the thing that fails.
            }
        }

        return null;
    }
}
