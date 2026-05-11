using System.Security.Cryptography;
using System.Text;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;

namespace Wolverine.Http.Diagnostics;

/// <summary>
/// <see cref="IHttpGraphUsageSource"/> implementation that walks the
/// in-process <see cref="HttpGraph"/> and surfaces it as an
/// <see cref="HttpGraphUsage"/> snapshot for CritterWatch.
/// </summary>
/// <remarks>
/// <para>
/// Pure descriptor production — does not mutate the graph, does not
/// emit code. OpenAPI shape is sourced via
/// <see cref="IApiDescriptionGroupCollectionProvider"/> when present,
/// which is registered by <c>AddEndpointsApiExplorer()</c> on every
/// modern ASP.NET Core HTTP host. When the host opted into
/// <c>Microsoft.AspNetCore.OpenApi</c>'s document service, the OpenAPI
/// extractor uses the richer schema producer there. Swashbuckle is
/// never referenced.
/// </para>
/// <para>
/// Versioned chains (clones produced by
/// <c>WolverineApiVersioningOptions</c>) are collapsed into a single
/// descriptor with <see cref="ApiVersionDescriptor.Versions"/> listing
/// every concrete version covered. Frontend renders the list as
/// version chips on the row.
/// </para>
/// </remarks>
internal sealed class HttpGraphUsageSource : IHttpGraphUsageSource
{
    private readonly WolverineHttpOptions _options;
    private readonly WolverineOptions _wolverineOptions;

    public HttpGraphUsageSource(WolverineHttpOptions options, WolverineOptions wolverineOptions)
    {
        _options = options;
        _wolverineOptions = wolverineOptions;
    }

    public Uri Subject { get; } = new("wolverine-http://main");

    public Task<HttpGraphUsage?> TryCreateUsage(IServiceProvider services, CancellationToken token)
    {
        var graph = _options.Endpoints;
        if (graph is null) return Task.FromResult<HttpGraphUsage?>(null);

        var usage = new HttpGraphUsage
        {
            Subject = "Wolverine.Http",
            SubjectUri = Subject,
            ServiceName = _wolverineOptions.ServiceName,
            WolverineHttpVersion = typeof(HttpChain).Assembly.GetName().Version?.ToString(),
            WarmUpRoutes = _options.WarmUpRoutes.ToString(),
            ServiceProviderSource = _options.ServiceProviderSource.ToString(),
            AutoAntiforgeryOnFormEndpoints = _options._autoAntiforgeryOnFormEndpoints,
            GlobalRoutePrefix = _options.GlobalRoutePrefix,
            ApiVersioningEnabled = _options.ApiVersioning is not null,
            NamespacePrefixes = _options.NamespacePrefixes
                .Select(p => new NamespacePrefixDescriptor { Namespace = p.Namespace, Prefix = p.Prefix })
                .ToList(),
            ResourceWriterPolicyNames = graph.WriterPolicies
                .Select(p => p.GetType().FullNameInCode())
                .ToList(),
            PolicyNames = _options.Policies
                .Select(p => p.GetType().FullNameInCode())
                .ToList(),
            TenantDetectionStrategies = _options.TenantId is { } detection
                ? collectTenantStrategies(_options)
                : new List<string>()
        };

        usage.AddTag("http");

        // ApiExplorer (registered by AddEndpointsApiExplorer()) gives us the full
        // ApiDescription per chain. Probe optionally — Wolverine.Http is happy
        // running without it; the OpenApi tab will just stay empty.
        var apiDescriptions = services.GetService<IApiDescriptionGroupCollectionProvider>();

        // Group chains by (route + method) to collapse multi-version clones.
        // Each clone has a distinct ApiVersion but identical handler+route, so
        // the (Route, HttpMethod, HandlerType, MethodName) tuple is the right
        // collapse key.
        var collapsed = graph.Chains
            .Where(c => c.RoutePattern is not null)
            .GroupBy(c => (
                c.RoutePattern!.RawText,
                Method: c.HttpMethods.OrderBy(m => m).Join(","),
                HandlerType: c.Method.HandlerType.FullNameInCode(),
                c.Method.Method.Name));

        foreach (var group in collapsed.OrderBy(g => g.Key.RawText))
        {
            var primary = group.First();
            var descriptor = buildChainDescriptor(primary, group.ToArray(), apiDescriptions, services);
            usage.Chains.Add(descriptor);
        }

        return Task.FromResult<HttpGraphUsage?>(usage);
    }

    private static List<string> collectTenantStrategies(WolverineHttpOptions options)
    {
        // TenantIdDetection.Strategies is internal, so we surface what we can
        // see — the fact that the policy is registered. Concrete strategy
        // type names are pulled if exposed; otherwise the list is left empty.
        try
        {
            var strategies = options.TenantId.GetType()
                .GetProperty("Strategies")?
                .GetValue(options.TenantId) as System.Collections.IEnumerable;

            if (strategies is null) return new List<string>();
            return strategies.Cast<object>().Select(s => s.GetType().FullNameInCode()).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private HttpChainDescriptor buildChainDescriptor(
        HttpChain chain,
        HttpChain[] versionGroup,
        IApiDescriptionGroupCollectionProvider? apiDescriptions,
        IServiceProvider services)
    {
        var route = chain.RoutePattern!.RawText ?? string.Empty;
        var methods = chain.HttpMethods.ToList();
        var chainId = computeStableId(route, methods, chain.Method.HandlerType.FullNameInCode(), chain.Method.Method.Name);

        var descriptor = new HttpChainDescriptor
        {
            Subject = $"Wolverine.Http.HttpChain[{route}]",
            ChainId = chainId,
            Route = route,
            HttpMethods = methods,
            RouteName = chain.RouteName,
            Order = chain.Order,
            DisplayName = chain.DisplayName ?? string.Empty,
            OperationId = chain.OperationId,
            HasExplicitOperationId = chain.HasExplicitOperationId,
            EndpointSummary = chain.EndpointSummary,
            EndpointDescription = chain.EndpointDescription,
            EndpointTypeFullName = chain.EndpointType.FullNameInCode(),
            MethodName = chain.Method.Method.Name,
            MethodSignature = chain.Method.MethodSignature,
            RequestType = chain.RequestType is { } req && req != typeof(void) ? TypeDescriptor.For(req) : null,
            ResourceType = chain.ResourceType is { } res && res != typeof(void) ? TypeDescriptor.For(res) : null,
            IsFormData = chain.IsFormData,
            NoContent = chain.NoContent,
            RequiresOutbox = chain.RequiresOutbox(),
            ConnegMode = chain.ConnegMode.ToString(),
            ServiceProviderSource = chain.ServiceProviderSource.ToString(),
            TenancyMode = chain.TenancyMode?.ToString(),
            IsTransactional = chain.IsTransactional
        };

        // OpenAPI tags — surface chain.Tags keys that are tagged as OpenAPI tags.
        // The tag dictionary here is generic (Wolverine uses it for many things);
        // operators care about the OpenAPI subset, but at this level we surface
        // them all and let the frontend trim with chip filtering.
        descriptor.Tags = chain.Tags
            .Select(p => $"{p.Key}={p.Value}")
            .ToList();

        // Cascading message types — non-resource Method.Creates members.
        var creates = chain.Method.Creates.ToArray();
        var resourceVariable = creates.FirstOrDefault();
        var cascading = creates
            .Where(v => v != resourceVariable || chain.NoContent)
            .Select(v => v.VariableType)
            .Where(t => t != typeof(void))
            .Distinct()
            .ToArray();
        descriptor.CascadingMessageTypes = cascading.Select(TypeDescriptor.For).ToList();

        // Service dependencies — surface what the chain resolves at runtime.
        descriptor.ServiceDependencies = readServiceDependencies(chain, services);

        descriptor.Middleware = describeFrames(chain.Middleware, "Middleware");
        descriptor.Postprocessors = describeFrames(chain.Postprocessors, "Postprocessor");

        // API version info. When versionGroup has >1 entries, this is a
        // collapsed multi-version chain; collect every version + sunset.
        descriptor.ApiVersion = buildApiVersionDescriptor(chain, versionGroup);

        // OpenAPI shape — pull the matching ApiDescription from ApiExplorer.
        if (apiDescriptions is not null)
        {
            descriptor.OpenApi = OpenApiDescriptorBuilder.TryBuildForWolverine(chain, apiDescriptions);
        }

        return descriptor;
    }

    private List<TypeDescriptor> readServiceDependencies(HttpChain chain, IServiceProvider services)
    {
        try
        {
            var container = services.GetService<IServiceContainer>();
            if (container is null) return new List<TypeDescriptor>();

            return chain
                .ServiceDependencies(container, Type.EmptyTypes)
                .Where(t => t != typeof(IServiceProvider))
                .Distinct()
                .Select(TypeDescriptor.For)
                .ToList();
        }
        catch
        {
            return new List<TypeDescriptor>();
        }
    }

    private static List<MiddlewareStepDescriptor> describeFrames(IEnumerable<Frame> frames, string kind)
    {
        var list = new List<MiddlewareStepDescriptor>();
        foreach (var frame in frames)
        {
            var step = new MiddlewareStepDescriptor
            {
                Kind = kind,
                Description = frame.ToString() ?? frame.GetType().FullNameInCode()
            };

            if (frame is MethodCall call)
            {
                step.TypeFullName = call.HandlerType.FullNameInCode();
                step.MethodName = call.Method.Name;
            }
            else
            {
                step.TypeFullName = frame.GetType().FullNameInCode();
            }

            list.Add(step);
        }

        return list;
    }

    private static ApiVersionDescriptor? buildApiVersionDescriptor(HttpChain chain, HttpChain[] versionGroup)
    {
        if (chain.IsApiVersionNeutral)
        {
            return new ApiVersionDescriptor { IsNeutral = true };
        }

        if (chain.ApiVersion is null && versionGroup.Length == 1)
        {
            return null;
        }

        var versions = versionGroup
            .Select(c => c.ApiVersion?.ToString())
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct()
            .Cast<string>()
            .ToList();

        var deprecated = versionGroup.Any(c => c.DeprecationPolicy is not null);
        var sunset = versionGroup
            .Select(c => c.SunsetPolicy?.Date)
            .FirstOrDefault(d => d is not null);

        return new ApiVersionDescriptor
        {
            Version = chain.ApiVersion?.ToString(),
            IsDeprecated = deprecated,
            Sunset = sunset,
            Versions = versions
        };
    }

    private static string computeStableId(string route, List<string> methods, string handlerType, string methodName)
        => HttpChainIds.ComputeStableId(route, methods, handlerType, methodName);
}

/// <summary>
/// Public façade over the same stable-id hash <see cref="HttpGraphUsageSource"/>
/// stamps onto every <c>HttpChainDescriptor</c>. Exposed for downstream
/// integration packages (e.g. <c>Wolverine.CritterWatch.Http</c>) that need to
/// match a runtime <see cref="HttpChain"/> back to a CritterWatch-side
/// descriptor by chain id without round-tripping through the descriptor pipeline.
/// </summary>
public static class HttpChainIds
{
    /// <summary>
    /// Compute the stable 16-character hex chain id for a chain identified by
    /// route, HTTP method set, handler type full name, and handler method
    /// name. The hash is stable across rebuilds so deep links and detail-page
    /// URLs survive deploys.
    /// </summary>
    public static string ComputeStableId(string route, IEnumerable<string> methods, string handlerType, string methodName)
    {
        var input = $"{methods.OrderBy(m => m).Join(",")}::{route}::{handlerType}.{methodName}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// Convenience overload that pulls route + methods + handler from a live
    /// <see cref="HttpChain"/>. Mirrors <see cref="HttpGraphUsageSource"/>'s
    /// own usage so identifiers stay in lockstep.
    /// </summary>
    public static string For(HttpChain chain)
    {
        var route = chain.RoutePattern?.RawText ?? string.Empty;
        return ComputeStableId(route, chain.HttpMethods, chain.Method.HandlerType.FullNameInCode(), chain.Method.Method.Name);
    }
}
