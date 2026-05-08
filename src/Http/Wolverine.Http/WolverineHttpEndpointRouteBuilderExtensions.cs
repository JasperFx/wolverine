using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.IoC;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wolverine.Configuration;
using JasperFx.Events;
using Wolverine.Configuration.Capabilities;
using Wolverine.Http.ApiVersioning;
using Wolverine.Http.CodeGen;
using Wolverine.Http.Diagnostics;
using Wolverine.Http.Transport;
using Wolverine.Http.Validation;
using Wolverine.Http.Validation.Internals;
using Wolverine.Middleware;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Wolverine.Http;

public class WolverineRequiredException : Exception
{
    public WolverineRequiredException(Exception? innerException) : base(
        "Wolverine is either not added to this application through IHostBuilder.UseWolverine() or is invalid",
        innerException)
    {
    }
}

public static class WolverineHttpEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Just a helper to configure the correct JsonOptions used by both Wolverine and Minimal API
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    public static void ConfigureSystemTextJsonForWolverineOrMinimalApi(this IServiceCollection services,
        Action<JsonOptions> configure)
    {
        services.Configure<JsonOptions>(configure);
    }

    /// <summary>
    /// Use the request body of type T to immediately invoke the incoming command with Wolverine
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="url"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static RouteHandlerBuilder MapPostToWolverine<T>(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")]string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(T)));
        return endpoints.MapPost(url,
            ([FromBody] T message, HttpContext context) => invoker.Value.InvokeAsync(message!,
                MessageBus.Build(runtime, context.TraceIdentifier), context.RequestAborted));
    }

    /// <summary>
    /// Use the request body of type T to immediately invoke the incoming command with Wolverine
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="url"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static RouteHandlerBuilder MapPutToWolverine<T>(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")]string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(T)));
        return endpoints.MapPut(url,
            ([FromBody] T message, HttpContext context) => invoker.Value.InvokeAsync(message!,
                MessageBus.Build(runtime, context.TraceIdentifier), context.RequestAborted));
    }

    /// <summary>
    /// Use the request body of type T to immediately invoke the incoming command with Wolverine
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="url"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static RouteHandlerBuilder MapDeleteToWolverine<T>(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")]string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(T)));
        return endpoints.MapDelete(url,
            ([FromBody] T message, HttpContext context) => invoker.Value.InvokeAsync(message!,
                MessageBus.Build(runtime, context.TraceIdentifier), context.RequestAborted));
    }

    /// <summary>
    /// Use the request body of type TRequest to immediately invoke the incoming command with Wolverine and return
    /// the response TResponse back to the caller
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="url"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <returns></returns>
    public static RouteHandlerBuilder MapPostToWolverine<TRequest, TResponse>(this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(TRequest)));
        return endpoints.MapPost(url,
            ([FromBody] TRequest message, HttpContext context) => invoker.Value.InvokeAsync<TResponse>(message!,
                MessageBus.Build(runtime, context.TraceIdentifier), context.RequestAborted));
    }

    /// <summary>
    /// Use the request body of type TRequest to immediately invoke the incoming command with Wolverine and return
    /// the response TResponse back to the caller
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="url"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <returns></returns>
    public static RouteHandlerBuilder MapPutToWolverine<TRequest, TResponse>(this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(TRequest)));
        return endpoints.MapPut(url,
            ([FromBody] TRequest message, HttpContext context) => invoker.Value.InvokeAsync<TResponse>(message!,
                MessageBus.Build(runtime, context.TraceIdentifier), context.RequestAborted));
    }

    /// <summary>
    /// Use the request body of type TRequest to immediately invoke the incoming command with Wolverine and return
    /// the response TResponse back to the caller
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="url"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <returns></returns>
    public static RouteHandlerBuilder MapDeleteToWolverine<TRequest, TResponse>(this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(TRequest)));
        return endpoints.MapDelete(url,
            ([FromBody] TRequest message, HttpContext context) => invoker.Value.InvokeAsync<TResponse>(message!,
                MessageBus.Build(runtime, context.TraceIdentifier), context.RequestAborted));
    }

    /// <summary>
    /// Add the necessary IoC service registrations for Wolverine.HTTP
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddWolverineHttp(this IServiceCollection services)
    {
        services.AddType(typeof(IApiDescriptionProvider), typeof(WolverineApiDescriptionProvider),
            ServiceLifetime.Singleton);
        services.AddSingleton<WolverineHttpOptions>();
        services.AddSingleton<NewtonsoftHttpSerialization>();
        services.AddSingleton<HttpTransportExecutor>();

        services.AddSingleton(typeof(IProblemDetailSource<>), typeof(ProblemDetailSource<>));
        services.AddSingleton<MatcherPolicy, ContentTypeEndpointSelectorPolicy>();

        services.AddSingleton<ICapabilityDescriptor, HttpCapabilityDescriptor>();

        // Issue #84 — surface the Wolverine HTTP graph as an
        // IHttpGraphUsageSource so ServiceCapabilities.HttpGraphs picks
        // it up at the same point as event-stores / document-stores /
        // db-contexts. The corresponding HttpCapabilityDescriptor stays
        // registered for back-compat with monitoring agents that haven't
        // shipped the richer reader yet.
        services.AddSingleton<IHttpGraphUsageSource, HttpGraphUsageSource>();

        // Registered unconditionally — harmless when no versioned endpoint uses it.
        services.AddSingleton<ApiVersionHeaderWriter>(sp =>
        {
            var httpOptions = sp.GetRequiredService<WolverineHttpOptions>();
            var versioningOptions = httpOptions.ApiVersioning ?? new WolverineApiVersioningOptions();
            return new ApiVersionHeaderWriter(versioningOptions);
        });

        services.ConfigureWolverine(opts =>
        {
            opts.CodeGeneration.Sources.Add(new NullableHttpContextSource());
        });

        return services;
    }

    /// <summary>
    ///     Discover and add Wolverine HTTP endpoints to your ASP.Net Core system
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="configure"></param>
    /// <exception cref="WolverineRequiredException"></exception>
    public static void MapWolverineEndpoints(this IEndpointRouteBuilder endpoints,
        Action<WolverineHttpOptions>? configure = null)
    {
        var runtime = GetWolverineRuntime(endpoints);

        runtime.WarnIfAnyAsyncExtensions();

        var serviceProvider = endpoints.ServiceProvider;
        var options = serviceProvider.GetService<WolverineHttpOptions>();
        if (options == null)
        {
            throw new InvalidOperationException($"Required usage of IServiceCollection.{nameof(AddWolverineHttp)}() is necessary for Wolverine.HTTP to function correctly");
        }

        // This let's Wolverine weave in middleware that might return ProblemDetails
        runtime.Options.CodeGeneration.AddContinuationStrategy<ProblemDetailsContinuationPolicy>();

        // This let's Wolverine weave in middleware that might return IResult
        runtime.Options.CodeGeneration.AddContinuationStrategy<ResultContinuationPolicy>();

        // Making sure this exists
        options.TenantIdDetection.Services = serviceProvider; // Hokey, but let this go
        options.Endpoints = new HttpGraph(runtime.Options, serviceProvider.GetRequiredService<IServiceContainer>());

        configure?.Invoke(options);

        options.Policies.Add(new ProblemDetailsFromMiddleware());

        // If ApiVersioning is enabled, append a finalization policy that re-positions the header
        // writer to index 0 of every relevant chain after every user-supplied policy has run.
        // FluentValidation / DataAnnotations / RequestId / TenantId middleware all also insert at
        // index 0; the writer must outrank them so its OnStarting callback registers before any
        // short-circuit return. See ApiVersionHeaderFinalizationPolicy.
        var versioningPolicy = options.Policies.OfType<ApiVersioning.ApiVersioningPolicy>().FirstOrDefault();
        if (versioningPolicy is not null)
        {
            options.Policies.Add(new ApiVersioning.ApiVersionHeaderFinalizationPolicy(versioningPolicy.ChainsRequiringHeaderEmission));
        }
        
        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            options.WarmUpRoutes = RouteWarmup.Lazy;
        }

        options.JsonSerializerOptions = new Lazy<JsonSerializerOptions>(() => serviceProvider.GetService<IOptions<JsonOptions>>()?.Value?.SerializerOptions ?? new JsonSerializerOptions());

        options.Endpoints.AutoAntiforgeryOnFormEndpoints = options._autoAntiforgeryOnFormEndpoints;
        options.Endpoints.DiscoverEndpoints(options);
        runtime.Options.Parts.Add(options.Endpoints);

        serviceProvider.GetRequiredService<WolverineSupplementalCodeFiles>().Collections.Add(options.Endpoints);

        endpoints.DataSources.Add(options.Endpoints);
    }

    internal static WolverineRuntime GetWolverineRuntime(IEndpointRouteBuilder endpoints)
    {
        WolverineRuntime runtime;

        try
        {
            runtime = (WolverineRuntime)endpoints.ServiceProvider.GetRequiredService<IWolverineRuntime>();
        }
        catch (Exception e)
        {
            throw new WolverineRequiredException(e);
        }

        return runtime;
    }
}