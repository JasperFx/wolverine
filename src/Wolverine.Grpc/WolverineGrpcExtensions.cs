using System.Reflection;
using Grpc.AspNetCore.Server;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.RuntimeCompiler;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Grpc;

/// <summary>
/// Extension methods for integrating Wolverine with ASP.NET Core gRPC services.
/// </summary>
public static class WolverineGrpcExtensions
{
    // Cache the open-generic MapGrpcService<TService> method to avoid repeated reflection.
    private static readonly MethodInfo MapGrpcServiceMethod =
        typeof(GrpcEndpointRouteBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(GrpcEndpointRouteBuilderExtensions.MapGrpcService)
                         && m.IsGenericMethodDefinition
                         && m.GetParameters().Length == 1);

    /// <summary>
    /// Adds Wolverine's gRPC integration to the service collection. This method
    /// registers the Wolverine-gRPC infrastructure. Callers must also register a
    /// gRPC host separately — use <c>services.AddCodeFirstGrpc()</c> for code-first
    /// services or <c>services.AddGrpc()</c> for proto-first services.
    /// </summary>
    /// <remarks>
    /// The call is idempotent — only the first invocation wires registrations,
    /// subsequent calls are no-ops (matching <c>opts.UseGrpcRichErrorDetails()</c>'s
    /// marker pattern). Without this guard, repeat calls would stack the
    /// <see cref="WolverineGrpcExceptionInterceptor"/> twice, doubling exception
    /// translation work and log output.
    /// </remarks>
    public static IServiceCollection AddWolverineGrpc(this IServiceCollection services)
        => AddWolverineGrpc(services, configure: null);

    /// <summary>
    /// Adds Wolverine's gRPC integration to the service collection and applies caller-supplied
    /// configuration to the singleton <see cref="WolverineGrpcOptions"/>. Idempotent — repeat
    /// invocations re-run <paramref name="configure"/> against the same options instance, so
    /// additive registrations (e.g., <c>opts.AddMiddleware&lt;T&gt;()</c>) accumulate but service
    /// registrations are not duplicated.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configure">Optional configuration callback for <see cref="WolverineGrpcOptions"/>.</param>
    public static IServiceCollection AddWolverineGrpc(
        this IServiceCollection services,
        Action<WolverineGrpcOptions>? configure)
    {
        var options = EnsureOptionsRegistered(services);

        if (services.Any(x => x.ServiceType == typeof(WolverineGrpcMarker)))
        {
            configure?.Invoke(options);
            return services;
        }

        services.AddSingleton<WolverineGrpcMarker>();

        services.AddSingleton<GrpcGraph>(sp =>
        {
            var runtime = (WolverineRuntime)sp.GetRequiredService<IWolverineRuntime>();
            var container = sp.GetRequiredService<IServiceContainer>();
            return new GrpcGraph(runtime.Options, container);
        });

        services.AddSingleton<WolverineGrpcExceptionInterceptor>();
        services.Configure<GrpcServiceOptions>(opts =>
        {
            opts.Interceptors.Add<WolverineGrpcExceptionInterceptor>();
        });

        configure?.Invoke(options);

        return services;
    }

    private static WolverineGrpcOptions EnsureOptionsRegistered(IServiceCollection services)
    {
        // The options instance must be reachable for the configure callback BEFORE the
        // marker check returns, so additive customizations on a second AddWolverineGrpc()
        // call land on the same singleton rather than silently dropping.
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(WolverineGrpcOptions))
            ?.ImplementationInstance as WolverineGrpcOptions;
        if (existing != null) return existing;

        var options = new WolverineGrpcOptions();
        services.AddSingleton(options);
        return options;
    }

    internal sealed class WolverineGrpcMarker
    {
    }

    /// <summary>
    /// Discovers and maps all gRPC service types found in the assemblies already
    /// scanned by Wolverine. A type is discovered when:
    /// <list type="bullet">
    ///   <item>Its name ends with <c>GrpcService</c>, or</item>
    ///   <item>It is decorated with <see cref="WolverineGrpcServiceAttribute"/>.</item>
    /// </list>
    /// Proto-first abstract stubs decorated with <see cref="WolverineGrpcServiceAttribute"/>
    /// trigger Wolverine code-generation of a concrete wrapper that forwards each
    /// unary RPC to <see cref="IMessageBus.InvokeAsync{T}"/>.
    ///
    /// Call this inside <c>app.UseEndpoints()</c> or directly on the <see cref="WebApplication"/>
    /// after <c>app.UseRouting()</c>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder from ASP.NET Core.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapWolverineGrpcServices(this IEndpointRouteBuilder endpoints)
    {
        var services = endpoints.ServiceProvider;
        var runtime = (WolverineRuntime)services.GetRequiredService<IWolverineRuntime>();
        var assemblies = runtime.Options.Assemblies;

        foreach (var type in FindCodeFirstServiceTypes(assemblies))
        {
            MapGrpcServiceMethod.MakeGenericMethod(type).Invoke(null, [endpoints]);
        }

        var graph = services.GetService<GrpcGraph>();
        if (graph != null)
        {
            MapProtoFirstServices(endpoints, services, graph);
        }

        return endpoints;
    }

    private static void MapProtoFirstServices(IEndpointRouteBuilder endpoints, IServiceProvider services, GrpcGraph graph)
    {
        if (graph.Chains.Count == 0)
        {
            graph.DiscoverServices();
        }

        if (graph.Chains.Count == 0) return;

        var runtime = (WolverineRuntime)services.GetRequiredService<IWolverineRuntime>();

        // Register with Options.Parts so CLI diagnostics ('dotnet run -- describe',
        // 'wolverine-diagnostics describe-routing <MessageType>') list proto-first gRPC services
        // alongside handlers and HTTP endpoints.
        if (!runtime.Options.Parts.Contains(graph))
        {
            runtime.Options.Parts.Add(graph);
        }

        // Expose this graph to Wolverine's supplemental code-file pipeline so that 'dotnet run codegen'
        // commands and Type load modes can see the generated wrappers alongside handler and HTTP chains.
        var supplemental = services.GetRequiredService<WolverineSupplementalCodeFiles>();
        if (!supplemental.Collections.Contains(graph))
        {
            supplemental.Collections.Add(graph);
        }

        foreach (var chain in graph.Chains)
        {
            chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, services);

            if (chain.GeneratedType == null)
            {
                throw new InvalidOperationException(
                    $"Failed to resolve the generated wrapper type for proto-first gRPC stub {chain.StubType.FullNameInCode()}. "
                    + $"Generated source was:\n{chain.SourceCode}");
            }

            MapGrpcServiceMethod.MakeGenericMethod(chain.GeneratedType).Invoke(null, [endpoints]);
        }
    }

    /// <summary>
    /// Returns all concrete, non-abstract types in <paramref name="assemblies"/> that
    /// qualify as hand-written (code-first / M3) Wolverine-managed gRPC services.
    /// Proto-first stubs (abstract classes) are excluded and handled separately via <see cref="GrpcGraph"/>.
    /// </summary>
    public static IEnumerable<Type> FindGrpcServiceTypes(IEnumerable<Assembly> assemblies)
    {
        return FindCodeFirstServiceTypes(assemblies);
    }

    private static IEnumerable<Type> FindCodeFirstServiceTypes(IEnumerable<Assembly> assemblies)
    {
        return assemblies
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t.IsClass && !t.IsAbstract && IsCodeFirstGrpcServiceType(t));
    }

    private static bool IsCodeFirstGrpcServiceType(Type type)
    {
        return type.Name.EndsWith("GrpcService", StringComparison.Ordinal)
               || type.IsDefined(typeof(WolverineGrpcServiceAttribute), inherit: false);
    }
}
