using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wolverine.Http.Commands;

/// <summary>
/// Thin reflection wrapper around the <c>Microsoft.Extensions.ApiDescriptions.IDocumentProvider</c>
/// service that <c>AddOpenApi()</c> (the <c>Microsoft.AspNetCore.OpenApi</c> package) registers.
/// This is the very same service that Microsoft's <c>GetDocument.Insider</c> build tool resolves,
/// so the document we emit is functionally equivalent to the one produced by
/// <c>Microsoft.Extensions.ApiDescription.Server</c> — only without the full <c>IHost</c>
/// startup that triggers database/broker connectivity.
///
/// The interface is internal to <c>Microsoft.AspNetCore.OpenApi</c> and is matched against by
/// full type name across the loaded assemblies, exactly as <c>GetDocument.Insider</c> does it,
/// rather than taking a compile-time dependency on a non-public type.
/// </summary>
internal sealed class OpenApiDocumentProvider
{
    // The well-known full name of the build-time document provider contract. This name is shared
    // (by string match) between Microsoft.Extensions.ApiDescription.Server and the various
    // implementations (Microsoft.AspNetCore.OpenApi, NSwag, Swashbuckle), so resolving by name
    // keeps us decoupled from any one provider package.
    private const string DocumentProviderTypeName = "Microsoft.Extensions.ApiDescriptions.IDocumentProvider";

    // The Microsoft.AspNetCore.OpenApi options type whose configured OpenApiVersion we read so we can
    // invoke the scope-safe 3-argument GenerateAsync overload. See GenerateAsync below.
    private const string OpenApiOptionsTypeName = "Microsoft.AspNetCore.OpenApi.OpenApiOptions";

    private readonly IServiceProvider _services;
    private readonly object _service;
    private readonly MethodInfo _getDocumentNames;
    private readonly MethodInfo _generateAsync;

    private OpenApiDocumentProvider(IServiceProvider services, object service, MethodInfo getDocumentNames,
        MethodInfo generateAsync)
    {
        _services = services;
        _service = service;
        _getDocumentNames = getDocumentNames;
        _generateAsync = generateAsync;
    }

    /// <summary>
    /// Attempt to resolve the registered OpenAPI document provider out of the application's
    /// service provider. Returns null when no provider is registered — typically because the
    /// application never called <c>builder.Services.AddOpenApi()</c>.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Dev/build-time 'openapi' CLI command. The document provider contract is matched by full type name against loaded provider packages (Microsoft.AspNetCore.OpenApi/NSwag/Swashbuckle), mirroring Microsoft's own GetDocument.Insider tool. Never runs on an AOT-published hot path.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "GenerateAsync(string, TextWriter) and GetDocumentNames() are public members of the shared IDocumentProvider contract; they are present on every provider implementation that GetDocument.Insider also invokes.")]
    public static OpenApiDocumentProvider? Resolve(IServiceProvider services)
    {
        // The IDocumentProvider contract is shared *source* compiled independently into each provider
        // package (Microsoft.AspNetCore.OpenApi, Swashbuckle, NSwag). Those are distinct CLR types
        // despite the identical full name, so an application that references more than one (e.g. a test
        // project that pulls in both) can have several. Probe each and use the first whose service is
        // actually registered — that is the provider the application opted into via AddOpenApi()/etc.
        foreach (var providerType in FindTypes(DocumentProviderTypeName))
        {
            var service = services.GetService(providerType);
            if (service == null)
            {
                continue;
            }

            var getDocumentNames = providerType.GetMethod("GetDocumentNames", Type.EmptyTypes);
            var generateAsync = providerType.GetMethod("GenerateAsync", [typeof(string), typeof(TextWriter)]);

            if (getDocumentNames == null || generateAsync == null)
            {
                continue;
            }

            return new OpenApiDocumentProvider(services, service, getDocumentNames, generateAsync);
        }

        return null;
    }

    public IReadOnlyList<string> GetDocumentNames()
    {
        var result = _getDocumentNames.Invoke(_service, null);
        return result is IEnumerable<string> names ? names.ToArray() : [];
    }

    public async Task GenerateAsync(string documentName, TextWriter writer)
    {
        // Microsoft.AspNetCore.OpenApi's OpenApiDocumentProvider is a *singleton* that captures the
        // *root* service provider. Its 2-argument GenerateAsync(name, writer) resolves the *scoped*
        // IOptionsSnapshot<OpenApiOptions> directly from that root provider, which throws
        // "Cannot resolve scoped service ... from root provider" under DI scope validation (the
        // default in the Development environment).
        //
        // Its 3-argument overload, GenerateAsync(name, writer, OpenApiSpecVersion), takes the spec
        // version explicitly and creates its own scope internally — it never resolves a scoped service
        // from the root. So we read the configured version from the *singleton* IOptionsMonitor<OpenApiOptions>
        // (safe from the root) and call that overload. This keeps the command working regardless of the
        // host environment. Non-Microsoft providers (Swashbuckle/NSwag) fall back to the 2-arg overload.
        if (TryInvokeScopeSafe(documentName, writer, out var scopeSafe))
        {
            await scopeSafe!;
            return;
        }

        await (Task)_generateAsync.Invoke(_service, [documentName, writer])!;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Dev/build-time 'openapi' CLI command. Resolves the Microsoft.AspNetCore.OpenApi options/provider members reflectively to call the scope-safe GenerateAsync overload; never on an AOT-published hot path.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "OpenApiOptions.OpenApiVersion and the 3-arg GenerateAsync overload are public members of Microsoft.AspNetCore.OpenApi types; present whenever that package (and therefore this code path) is in play.")]
    [UnconditionalSuppressMessage("Trimming", "IL2055",
        Justification = "IOptionsMonitor<OpenApiOptions> is closed over a public framework options type; only reached for the Microsoft.AspNetCore.OpenApi provider in a dev/build CLI.")]
    [UnconditionalSuppressMessage("Trimming", "IL2076",
        Justification = "The reflectively-resolved OpenApiOptions type is a concrete public options class; closing IOptionsMonitor<TOptions> over it in this dev/build CLI cannot be trimmed away because the live registration keeps it.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType over IOptionsMonitor<OpenApiOptions> only runs in the dev/build 'openapi' CLI, never on an AOT-published hot path.")]
    private bool TryInvokeScopeSafe(string documentName, TextWriter writer, out Task? task)
    {
        task = null;

        try
        {
            var optionsType = FindTypes(OpenApiOptionsTypeName).FirstOrDefault();
            if (optionsType == null)
            {
                return false;
            }

            var monitor = _services.GetService(typeof(IOptionsMonitor<>).MakeGenericType(optionsType));
            var namedOptions = monitor?.GetType().GetMethod("Get", [typeof(string)])?.Invoke(monitor, [documentName]);
            var specVersion = namedOptions?.GetType().GetProperty("OpenApiVersion")?.GetValue(namedOptions);
            if (specVersion == null)
            {
                return false;
            }

            var generateWithVersion = _service.GetType()
                .GetMethod("GenerateAsync", [typeof(string), typeof(TextWriter), specVersion.GetType()]);
            if (generateWithVersion == null)
            {
                return false;
            }

            task = (Task)generateWithVersion.Invoke(_service, [documentName, writer, specVersion])!;
            return true;
        }
        catch (Exception)
        {
            // Any reflection mismatch (e.g. a non-Microsoft provider) falls back to the 2-arg overload.
            task = null;
            return false;
        }
    }

    private static IEnumerable<Type> FindTypes(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            Type? type = null;
            try
            {
                type = assembly.GetType(fullName, throwOnError: false);
            }
            catch (Exception)
            {
                // Some assemblies can throw when probed for types (e.g. ReflectionTypeLoadException
                // shapes). Those are never the OpenAPI provider package, so skip them.
            }

            if (type != null)
            {
                yield return type;
            }
        }
    }
}
