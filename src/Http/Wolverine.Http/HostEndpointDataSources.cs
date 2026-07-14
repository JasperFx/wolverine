using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Wolverine.Http;

/// <summary>
/// Makes every endpoint the host has mapped — minimal API, MVC, gRPC, Wolverine's own — visible to
/// ASP.NET Core's ApiExplorer before the host has started.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core routes ApiExplorer through a single <see cref="EndpointDataSource" /> singleton: a
/// CompositeEndpointDataSource wrapping the collection held by the (internal)
/// RouteOptions.EndpointDataSources. Endpoints mapped on an <see cref="IEndpointRouteBuilder" />
/// (<c>app.MapGet()</c>, <c>app.MapControllers()</c>, <c>app.MapWolverineEndpoints()</c>) land in that
/// builder's own DataSources immediately, but only reach the global collection when
/// <c>UseEndpoints()</c> runs — and on a <c>WebApplication</c> that happens inside StartAsync().
/// </para>
/// <para>
/// So an ApiExplorer read before the host starts sees an *empty* global collection. Worse, ASP.NET Core
/// caches the resulting ApiDescription collection against the MVC action-descriptor version, which
/// endpoint registration never bumps — so that first, premature read is what the host serves for the rest
/// of its life. Wolverine's own descriptions are start-independent (GH-3373: they are read straight off
/// the HttpGraph), which on a hybrid host turns the old empty document into something more dangerous: a
/// document listing every Wolverine route and silently omitting every minimal API and MVC one. See
/// GH-3421.
/// </para>
/// <para>
/// Publishing the route builder's data sources into the global collection ahead of the read closes the
/// gap. It is the same operation <c>UseEndpoints()</c> performs at start, with the same data source
/// instances — and UseEndpoints() de-dupes by reference, so it is a no-op there afterwards.
/// </para>
/// </remarks>
internal static class HostEndpointDataSources
{
    // RouteOptions.EndpointDataSources is internal to Microsoft.AspNetCore.Routing. It has been the
    // observable collection backing the EndpointDataSource singleton since it was introduced in .NET 6.
    // `the_aspnetcore_endpoint_data_source_collection_is_still_reachable` fails loudly if a future
    // ASP.NET Core moves it, rather than letting Wolverine quietly go back to serving partial documents.
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "RouteOptions.EndpointDataSources is an internal collection ASP.NET Core's own ConfigureRouteOptions/UseEndpoints write to and read from, so the property is always rooted by live framework code. TryPublish() degrades to a logged warning if it is ever unreachable.")]
    internal static PropertyInfo? EndpointDataSourcesProperty { get; } =
        typeof(RouteOptions).GetProperty("EndpointDataSources",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    // Serializes Wolverine's own writers against each other: ApiDescriptionGroupCollectionProvider caches
    // without synchronizing, so two simultaneous cold ApiExplorer reads both run the description providers
    // and hence both land here. It cannot exclude ASP.NET Core's UseEndpoints(), which writes the same
    // collection unlocked — but that writer only runs while the host is starting, and an ApiExplorer read
    // concurrent with host start is already unsafe in ASP.NET Core (it enumerates the very collection
    // UseEndpoints is mutating), with or without Wolverine. Uncontended in practice, and taken once per
    // ApiExplorer composition rather than per request.
    private static readonly object _lock = new();

    /// <summary>
    /// Publish the endpoints a host has mapped. Nothing to do for a host that is not an
    /// <see cref="IEndpointRouteBuilder" /> — a <c>WebApplication</c> is both.
    /// </summary>
    public static bool TryPublish(IHost host)
    {
        if (host is IEndpointRouteBuilder routeBuilder)
        {
            return TryPublish(routeBuilder);
        }

        return true;
    }

    /// <summary>
    /// Add every data source the route builder knows about to the global collection ASP.NET Core's
    /// ApiExplorer reads, skipping the ones already there. Safe to call repeatedly, and a no-op once the
    /// host has started and UseEndpoints() has published them itself.
    /// </summary>
    /// <param name="routeBuilder">
    /// The application's <em>root</em> route builder — the <c>WebApplication</c> — and never a nested one.
    /// A <c>RouteGroupBuilder</c>'s DataSources are the group's <em>inner</em> sources, which ASP.NET Core
    /// already publishes on the group's behalf (prefixed, and carrying the group's conventions) through the
    /// GroupDataSource it registered on the outer builder. Publishing those inner sources as well would
    /// register every endpoint in the group a second time, stripped of the prefix and the conventions — at
    /// a URL the application never mapped. <see cref="IsRoot" /> is the guard.
    /// </param>
    /// <returns>False if ASP.NET Core's internals could not be reached, meaning a pre-start read of the
    /// ApiExplorer would be missing the host's non-Wolverine endpoints.</returns>
    public static bool TryPublish(IEndpointRouteBuilder routeBuilder)
    {
        if (!tryGetGlobalDataSources(routeBuilder.ServiceProvider, out var global))
        {
            return false;
        }

        lock (_lock)
        {
            foreach (var dataSource in routeBuilder.DataSources)
            {
                // Reference equality, matching UseEndpoints(). Publishing anything other than the very
                // instances the route builder holds would leave UseEndpoints() to add them a second time
                // at start, and the router would then see every endpoint twice.
                if (!global.Contains(dataSource))
                {
                    global.Add(dataSource);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Is this the application's root route builder — the one holding the host's endpoints — rather than a
    /// nested builder such as a route group? A <c>WebApplication</c> is the root, and is also the IHost.
    /// </summary>
    public static bool IsRoot(IEndpointRouteBuilder routeBuilder)
    {
        return routeBuilder is IHost;
    }

    /// <summary>
    /// Has anything reached the global collection yet? Nothing has before the host starts, which is exactly
    /// when a read of it yields an incomplete document.
    /// </summary>
    public static bool AnyPublished(IServiceProvider services)
    {
        return tryGetGlobalDataSources(services, out var global) && global.Count > 0;
    }

    private static bool tryGetGlobalDataSources(IServiceProvider services,
        out ICollection<EndpointDataSource> dataSources)
    {
        dataSources = default!;

        if (EndpointDataSourcesProperty == null)
        {
            return false;
        }

        if (services.GetService<IOptions<RouteOptions>>()?.Value is not { } routeOptions)
        {
            return false;
        }

        if (EndpointDataSourcesProperty.GetValue(routeOptions) is not ICollection<EndpointDataSource> collection
            || collection.IsReadOnly)
        {
            return false;
        }

        dataSources = collection;
        return true;
    }
}
