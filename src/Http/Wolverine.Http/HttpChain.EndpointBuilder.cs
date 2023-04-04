using JasperFx.RuntimeCompiler;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Wolverine.Http;

public partial class HttpChain : IEndpointConventionBuilder
{
    private readonly List<Action<EndpointBuilder>> _builderConfigurations = new();

    /// <summary>
    /// Configure ASP.Net Core endpoint metadata
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public RouteHandlerBuilder Metadata { get; }

    public void Add(Action<EndpointBuilder> convention)
    {
        _builderConfigurations.Add(convention);
    }

    public RouteEndpoint BuildEndpoint()
    {
        var handler = new Lazy<HttpHandler>(() =>
        {
            this.InitializeSynchronously(_parent.Rules, _parent, _parent.Container);
            return (HttpHandler)_parent.Container.QuickBuild(_handlerType);
        });

        var builder = new RouteEndpointBuilder(c => handler.Value.Handle(c), RoutePattern, Order)
        {
            DisplayName = DisplayName
        };
        
        foreach (var configuration in _builderConfigurations)
        {
            configuration(builder);
        }

        Endpoint = (RouteEndpoint?)builder.Build();

        return Endpoint;
    }
}