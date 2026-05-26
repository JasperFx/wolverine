using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

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

    private readonly object _service;
    private readonly MethodInfo _getDocumentNames;
    private readonly MethodInfo _generateAsync;

    private OpenApiDocumentProvider(object service, MethodInfo getDocumentNames, MethodInfo generateAsync)
    {
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
        foreach (var providerType in FindDocumentProviderTypes())
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

            return new OpenApiDocumentProvider(service, getDocumentNames, generateAsync);
        }

        return null;
    }

    public IReadOnlyList<string> GetDocumentNames()
    {
        var result = _getDocumentNames.Invoke(_service, null);
        return result is IEnumerable<string> names ? names.ToArray() : [];
    }

    public Task GenerateAsync(string documentName, TextWriter writer)
    {
        return (Task)_generateAsync.Invoke(_service, [documentName, writer])!;
    }

    private static IEnumerable<Type> FindDocumentProviderTypes()
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
                type = assembly.GetType(DocumentProviderTypeName, throwOnError: false);
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
