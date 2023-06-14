using System.Text.Json;
using Lamar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Http.CodeGen;
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
    /// Use the request body of type T to immediately invoke the incoming command with Wolverine
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="url"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static RouteHandlerBuilder MapPostToWolverine<T>(this IEndpointRouteBuilder endpoints, string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(T)));
        return endpoints.MapPost(url,
            ([FromBody] T message, HttpContext context) => invoker.Value.InvokeAsync(message!,
                new MessageBus(runtime, context.TraceIdentifier), context.RequestAborted));
    }

    /// <summary>
    /// Use the request body of type T to immediately invoke the incoming command with Wolverine
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="url"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static RouteHandlerBuilder MapPutToWolverine<T>(this IEndpointRouteBuilder endpoints, string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(T)));
        return endpoints.MapPut(url,
            ([FromBody] T message, HttpContext context) => invoker.Value.InvokeAsync(message!,
                new MessageBus(runtime, context.TraceIdentifier), context.RequestAborted));
    }

    /// <summary>
    /// Use the request body of type T to immediately invoke the incoming command with Wolverine
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="url"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static RouteHandlerBuilder MapDeleteToWolverine<T>(this IEndpointRouteBuilder endpoints, string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(T)));
        return endpoints.MapDelete(url,
            ([FromBody] T message, HttpContext context) => invoker.Value.InvokeAsync(message!,
                new MessageBus(runtime, context.TraceIdentifier), context.RequestAborted));
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
        string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(TRequest)));
        return endpoints.MapPost(url,
            ([FromBody] TRequest message, HttpContext context) => invoker.Value.InvokeAsync<TResponse>(message!,
                new MessageBus(runtime, context.TraceIdentifier), context.RequestAborted));
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
        string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(TRequest)));
        return endpoints.MapPut(url,
            ([FromBody] TRequest message, HttpContext context) => invoker.Value.InvokeAsync<TResponse>(message!,
                new MessageBus(runtime, context.TraceIdentifier), context.RequestAborted));
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
        string url)
    {
        var runtime = GetWolverineRuntime(endpoints);
        var invoker = new Lazy<IMessageInvoker>(() => runtime.FindInvoker(typeof(TRequest)));
        return endpoints.MapDelete(url,
            ([FromBody] TRequest message, HttpContext context) => invoker.Value.InvokeAsync<TResponse>(message!,
                new MessageBus(runtime, context.TraceIdentifier), context.RequestAborted));
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

        var container = (IContainer)endpoints.ServiceProvider;

        // This let's Wolverine weave in middleware that might return IResult
        runtime.Options.CodeGeneration.AddContinuationStrategy<ResultContinuationPolicy>();

        // Making sure this exists
        var options = container.GetInstance<WolverineHttpOptions>();
        options.Endpoints = new HttpGraph(runtime.Options, container);

        configure?.Invoke(options);

        options.JsonSerializerOptions =
            container.TryGetInstance<JsonOptions>()?.SerializerOptions ?? new JsonSerializerOptions();

        options.Endpoints.DiscoverEndpoints(options);
        runtime.AdditionalDescribedParts.Add(options.Endpoints);

        container.GetInstance<WolverineSupplementalCodeFiles>().Collections.Add(options.Endpoints);

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