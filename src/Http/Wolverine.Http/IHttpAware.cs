using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Wolverine.Http.Resources;

namespace Wolverine.Http;

/// <summary>
/// Interface for resource types in Wolverine.Http that need to modify
/// how the HTTP response is formatted. Use this for additional headers
/// or customized status codes
/// </summary>
public interface IHttpAware : IEndpointMetadataProvider
{
    void Apply(HttpContext context);
}

internal class HttpAwarePolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
    {
        var matching = chains.Where(x => x.ResourceType != null && x.ResourceType.CanBeCastTo(typeof(IHttpAware)));
        foreach (var chain in matching)
        {
            var resource = chain.Method.Creates.FirstOrDefault(x => x.VariableType == chain.ResourceType);
            if (resource == null)
            {
                return;
            }

            var apply = new MethodCall(typeof(IHttpAware), nameof(IHttpAware.Apply))
            {
                Target = new CastVariable(resource, typeof(IHttpAware))
            };

            // This will have to run before any kind of resource writing
            chain.Postprocessors.Insert(0, apply);
        }
    }
}

public static class EndpointBuilderExtensions
{
    public static EndpointBuilder RemoveStatusCodeResponse(this EndpointBuilder builder, int statusCode)
    {
        builder.Metadata.RemoveAll(x => x is IProducesResponseTypeMetadata m && m.StatusCode == statusCode);
        return builder;
    }
}

/// <summary>
/// Base class for resource types that denote some kind of resource being created
/// in the system. Wolverine specific, and more efficient, version of Created<T> from ASP.Net Core
/// </summary>
public abstract record CreationResponse : IHttpAware
{
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        builder.RemoveStatusCodeResponse(200);

        var create = new MethodCall(method.DeclaringType, method).Creates.FirstOrDefault()?.VariableType;
        var metadata = new ProducesResponseTypeMetadata { Type = create, StatusCode = 201 };
        builder.Metadata.Add(metadata);
    }

    protected virtual string Url()
    {
        return string.Empty;
    }

    void IHttpAware.Apply(HttpContext context)
    {
        context.Response.Headers.Location = Url();
        context.Response.StatusCode = 201;
    }
}