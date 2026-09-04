using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using JasperFx.CodeGeneration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Http;

public partial class HttpGraph
{
    /// <summary>
    ///     GH-4156. The Wolverine.Http counterpart to <see cref="HandlerGraph.AssertPreBuiltTypesExist" />.
    ///     Endpoint chains are a separate <see cref="JasperFx.CodeGeneration.ICodeFileCollection" /> and were
    ///     deliberately left out of the GH-4151 fix, but they have the identical exposure: in
    ///     <see cref="TypeLoadMode.Static" /> there is no fallback, so an endpoint whose pre-generated type is
    ///     not in the application assembly can never be invoked. Until now the miss surfaced on the first HTTP
    ///     request to that route -- the host reported healthy in the meantime, and the deploy that caused it
    ///     was long finished. Attach every expected type up front instead, so the misconfiguration is a failed
    ///     start.
    /// </summary>
    /// <remarks>
    ///     Attaching is not merely a probe. <see cref="HttpChain" /> caches the resolved type in
    ///     <c>_handlerType</c>, so the types the loader would otherwise resolve one route at a time on their
    ///     first request are resolved here, which is what Static mode wanted in the first place.
    /// </remarks>
    internal void AssertPreBuiltTypesExist()
    {
        if (Rules.TypeLoadMode != TypeLoadMode.Static)
        {
            return;
        }

        // `codegen write` runs against an application whose generated types do not exist yet -- that is the
        // point of running it. Same guard as the registry fast path in DiscoverEndpoints.
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
            // The pre-generated HttpEndpointRegistry is deliberately not fatal, for the same reason the
            // handler-side HandlerRegistry is not: its absence only costs the cold-start scan skip in
            // DiscoverEndpoints, which already falls back to HttpChainSource.FindActions(). No request is
            // lost over it.
            if (file is HttpEndpointRegistryCodeFile)
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

        throw new MissingPreBuiltTypesException(describeMissingEndpointTypes(applicationAssembly, missing));
    }

    private string describeMissingEndpointTypes(Assembly applicationAssembly, List<ICodeFile> missing)
    {
        var message = new StringBuilder();

        message.AppendLine(
            $"Wolverine.Http is running in {nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Static)}, but {missing.Count} expected pre-built HTTP endpoint type(s) could not be loaded from the configured {nameof(WolverineOptions.ApplicationAssembly)} '{applicationAssembly.GetName().Name}':");

        foreach (var chain in missing.OfType<HttpChain>())
        {
            // The route, not just the generated type name -- an operator reading a failed deploy knows which
            // routes they have, and does not know what codegen called them.
            message.AppendLine($"  * {chain.RoutePattern?.RawText ?? chain.Description} ({chain})");
        }

        foreach (var file in missing.Where(x => x is not HttpChain))
        {
            message.AppendLine("  * " + file);
        }

        message.AppendLine();

        var elsewhere = findAssemblyHoldingGeneratedEndpointTypes(applicationAssembly);
        if (elsewhere != null)
        {
            message.AppendLine(
                $"Pre-generated Wolverine HTTP types were found in '{elsewhere.GetName().Name}' instead. 'dotnet run -- codegen write' emits its source into the entry project, while {nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Static)} loads pre-built types from {nameof(WolverineOptions)}.{nameof(WolverineOptions.ApplicationAssembly)}, so the two disagree.");
            message.AppendLine(
                $"If {nameof(WolverineOptions.ApplicationAssembly)} was only set so that Wolverine would discover endpoints living in another assembly, use opts.Discovery.{nameof(Configuration.HandlerDiscovery.IncludeAssembly)}(...) for that instead and leave {nameof(WolverineOptions.ApplicationAssembly)} as the entry assembly. Otherwise, point the generated code output at the project that builds '{applicationAssembly.GetName().Name}' with opts.CodeGeneration.{nameof(GenerationRules.GeneratedCodeOutputPath)}.");
        }
        else
        {
            message.AppendLine(
                "No pre-generated Wolverine HTTP types could be found in any assembly this application is using. Run 'dotnet run -- codegen write' as part of the build and compile its output into the application assembly, or run in TypeLoadMode.Auto.");
        }

        message.AppendLine();
        message.Append("See https://wolverinefx.net/guide/http/codegen.html");

        return message.ToString();
    }

    // Purely a diagnostic for the exception message above: say where the generated code actually landed,
    // because "the types are missing" and "the types are in the other assembly" call for different fixes.
    // The generated HttpEndpointRegistry is the marker -- codegen write always emits exactly one, alongside
    // the endpoint types, into whichever project it wrote to.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "ExportedTypes walk over candidate assemblies to locate misplaced codegen output; runs only on the startup failure path while building an exception message. See AOT guide.")]
    private Assembly? findAssemblyHoldingGeneratedEndpointTypes(Assembly applicationAssembly)
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
                if (candidate.ExportedTypes.Any(x => x.Name == HttpEndpointRegistry.GeneratedTypeName))
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
