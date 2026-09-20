using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

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
/// Publishing a view over the route builder's data sources into the global collection ahead of the read
/// closes the gap. The view is added once, from <c>MapWolverineEndpoints()</c> — on the thread composing
/// the application, before anything is running — and it yields nothing for a data source
/// <c>UseEndpoints()</c> has since published itself, so the host's endpoints are never registered twice.
/// </para>
/// <para>
/// It has to be a deferred view rather than a copy of the data sources for two reasons. The obvious one
/// is completeness: <c>MapControllers()</c>, another <c>MapGroup()</c>, a gRPC service mapped after
/// <c>MapWolverineEndpoints()</c> each add a new data source, and a pre-start read has to see those too.
/// The other is GH-4500: <c>RouteOptions.EndpointDataSources</c> is a plain <c>ObservableCollection</c>
/// that ASP.NET Core enumerates unsynchronized on the startup thread, so writing to it from anywhere else
/// — say, a monitoring observer reading the ApiExplorer as the runtime starts — kills that enumeration
/// with "Collection was modified" and takes host startup down with it. Wolverine therefore writes this
/// collection exactly where ASP.NET Core does, and reading the ApiExplorer never writes it at all.
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
    /// Make the endpoints the route builder knows about visible to the global collection ASP.NET Core's
    /// ApiExplorer reads. Idempotent, and must only ever be called from the thread composing the
    /// application — see the GH-4500 note on this class.
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

        if (global.OfType<UnpublishedEndpointDataSource>().Any(x => ReferenceEquals(x.RouteBuilder, routeBuilder)))
        {
            return true;
        }

        global.Add(new UnpublishedEndpointDataSource(routeBuilder, global));

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
    /// Has ASP.NET Core published the application's own endpoints yet? It does that in UseEndpoints() at
    /// start; before then a read of the global collection yields an incomplete document. Wolverine's own
    /// deferred view does not count — it is there from MapWolverineEndpoints() onwards.
    /// </summary>
    public static bool AnyPublished(IServiceProvider services)
    {
        return tryGetGlobalDataSources(services, out var global)
               && global.Any(x => x is not UnpublishedEndpointDataSource);
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

/// <summary>
/// The endpoints a route builder has mapped that ASP.NET Core has <em>not</em> published to
/// <c>RouteOptions.EndpointDataSources</c> itself yet — i.e. everything, until UseEndpoints() runs at host
/// start, and nothing afterwards.
/// </summary>
/// <remarks>
/// This is what <see cref="HostEndpointDataSources.TryPublish(IEndpointRouteBuilder)" /> registers, in
/// place of the route builder's own data sources. One registration, made while the application is being
/// composed, covers every endpoint mapped before or after <c>MapWolverineEndpoints()</c> — and because the
/// view subtracts whatever UseEndpoints() has already published, the endpoint never appears twice no
/// matter which side of host start it is read from.
/// </remarks>
internal sealed class UnpublishedEndpointDataSource : EndpointDataSource
{
    private readonly ICollection<EndpointDataSource> _global;

    public UnpublishedEndpointDataSource(IEndpointRouteBuilder routeBuilder, ICollection<EndpointDataSource> global)
    {
        RouteBuilder = routeBuilder;
        _global = global;
    }

    public IEndpointRouteBuilder RouteBuilder { get; }

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            var endpoints = new List<Endpoint>();
            foreach (var dataSource in RouteBuilder.DataSources.ToArray())
            {
                // Reference equality, matching UseEndpoints(). Once ASP.NET Core has published a data
                // source itself, this view has to stop answering for it or every endpoint in it matches
                // twice.
                if (_global.Contains(dataSource))
                {
                    continue;
                }

                endpoints.AddRange(dataSource.Endpoints);
            }

            return endpoints;
        }
    }

    // Deliberately never signals. The only thing that changes what this view answers is UseEndpoints()
    // publishing the real data sources, and that writes the global collection, which the composite source
    // is already watching.
    public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
}
