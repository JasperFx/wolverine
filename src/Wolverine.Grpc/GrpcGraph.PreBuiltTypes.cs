using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using JasperFx.CodeGeneration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Grpc;

public partial class GrpcGraph
{
    /// <summary>
    ///     GH-4156. The Wolverine.Grpc counterpart to <see cref="HandlerGraph.AssertPreBuiltTypesExist" /> and
    ///     <see cref="Http.HttpGraph.AssertPreBuiltTypesExist" />. All three chain flavors here -- proto-first,
    ///     code-first and hand-written -- resolve a generated wrapper by name out of the application assembly,
    ///     and in <see cref="TypeLoadMode.Static" /> there is no fallback when that lookup misses. Without this
    ///     the miss surfaces on the first RPC to that service, with the host reporting healthy in between.
    /// </summary>
    internal void AssertPreBuiltTypesExist()
    {
        if (Rules.TypeLoadMode != TypeLoadMode.Static)
        {
            return;
        }

        // `codegen write` runs against an application whose generated types do not exist yet -- that is the
        // point of running it. Same guard as the registry fast path in DiscoverServices.
        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            return;
        }

        var applicationAssembly = Rules.ApplicationAssembly;
        if (applicationAssembly == null)
        {
            return;
        }

        var collection = (ICodeFileCollection)this;
        var containingNamespace = collection.ToNamespace(Rules);

        var missing = new List<ICodeFile>();
        foreach (var file in collection.BuildFiles())
        {
            // The pre-generated GrpcServiceRegistry is deliberately not fatal, for the same reason the
            // handler and HTTP registries are not: its absence only costs the cold-start scan skip in
            // DiscoverServices, which already falls back to the GetExportedTypes walk. No RPC is lost over it.
            if (file is GrpcServiceRegistryCodeFile)
            {
                continue;
            }

            if (!file.AttachTypesSynchronously(Rules, applicationAssembly, Container.Services, containingNamespace))
            {
                missing.Add(file);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        throw new MissingPreBuiltTypesException(describeMissingServiceTypes(applicationAssembly, missing));
    }

    private string describeMissingServiceTypes(Assembly applicationAssembly, List<ICodeFile> missing)
    {
        var message = new StringBuilder();

        message.AppendLine(
            $"Wolverine.Grpc is running in {nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Static)}, but {missing.Count} expected pre-built gRPC service type(s) could not be loaded from the configured {nameof(WolverineOptions.ApplicationAssembly)} '{applicationAssembly.GetName().Name}':");

        foreach (var file in missing)
        {
            message.AppendLine("  * " + file);
        }

        message.AppendLine();

        var elsewhere = findAssemblyHoldingGeneratedServiceTypes(applicationAssembly);
        if (elsewhere != null)
        {
            message.AppendLine(
                $"Pre-generated Wolverine gRPC types were found in '{elsewhere.GetName().Name}' instead. 'dotnet run -- codegen write' emits its source into the entry project, while {nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Static)} loads pre-built types from {nameof(WolverineOptions)}.{nameof(WolverineOptions.ApplicationAssembly)}, so the two disagree.");
            message.AppendLine(
                $"Point the generated code output at the project that builds '{applicationAssembly.GetName().Name}' with opts.CodeGeneration.{nameof(GenerationRules.GeneratedCodeOutputPath)}, or leave {nameof(WolverineOptions.ApplicationAssembly)} as the entry assembly.");
        }
        else
        {
            message.AppendLine(
                "No pre-generated Wolverine gRPC types could be found in any assembly this application is using. Run 'dotnet run -- codegen write' as part of the build and compile its output into the application assembly, or run in TypeLoadMode.Auto.");
        }

        message.AppendLine();
        message.Append("See https://wolverinefx.net/guide/codegen.html");

        return message.ToString();
    }

    // Purely a diagnostic for the exception message above: say where the generated code actually landed,
    // because "the types are missing" and "the types are in the other assembly" call for different fixes.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "ExportedTypes walk over candidate assemblies to locate misplaced codegen output; runs only on the startup failure path while building an exception message. See AOT guide.")]
    private Assembly? findAssemblyHoldingGeneratedServiceTypes(Assembly applicationAssembly)
    {
        var candidates = new List<Assembly>();

        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly != null)
        {
            candidates.Add(entryAssembly);
        }

        candidates.AddRange(_options.Assemblies);

        foreach (var candidate in candidates.Distinct().Where(x => x != applicationAssembly && !x.IsDynamic))
        {
            try
            {
                if (candidate.ExportedTypes.Any(x => x.Name == GrpcServiceRegistry.GeneratedTypeName))
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
