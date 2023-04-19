using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.RuntimeCompiler;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Wolverine.Http.Resources;

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

    private void tryApplyAsEndpointMetadataProvider(Type type, RouteEndpointBuilder builder)
    {
        if (type != null && type.CanBeCastTo(typeof(IEndpointMetadataProvider)))
        {
            var applier = typeof(Applier<>).CloseAndBuildAs<IApplier>(type);
            applier.Apply(builder, Method.Method);
        }
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
        
        tryApplyAsEndpointMetadataProvider(ResourceType, builder);
        foreach (var parameter in Method.Method.GetParameters())
        {
            tryApplyAsEndpointMetadataProvider(parameter.ParameterType, builder);
        }
        

        if (ResourceType == null)
        {
            builder.RemoveStatusCodeResponse(200);
            builder.Metadata.Add(new ProducesResponseTypeMetadata{StatusCode = 204, Type = null});
        }

        Endpoint = (RouteEndpoint?)builder.Build();

        return Endpoint;
    }

    internal interface IApplier
    {
        void Apply(EndpointBuilder builder, MethodInfo method);
    }
    
    internal class Applier<T> : IApplier where T : IEndpointMetadataProvider
    {
        public void Apply(EndpointBuilder builder, MethodInfo method) => T.PopulateMetadata(method, builder);
    }
}