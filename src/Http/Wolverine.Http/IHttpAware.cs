using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Wolverine.Http.Resources;
using Wolverine.Runtime;

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
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var matching = chains.Where(x => x.ResourceType != null && x.ResourceType.CanBeCastTo(typeof(IHttpAware)));
        foreach (var chain in matching)
        {
            var resource = chain.Method.Creates.FirstOrDefault(x => x.VariableType == chain.ResourceType);
            if (resource == null)
            {
                return;
            }

            var apply = new ApplyHttpAware(resource);

            // This will have to run before any kind of resource writing
            chain.Postprocessors.Insert(0, apply);
        }
    }
}

internal class ApplyHttpAware : SyncFrame
{
    private readonly Variable _target;
    private Variable _httpContext;

    public ApplyHttpAware(Variable target)
    {
        _target = target;
        uses.Add(target);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("This response type customizes the HTTP response");
        writer.Write($"{nameof(HttpHandler.ApplyHttpAware)}({_target.Usage}, {_httpContext.Usage});");
        Next?.GenerateCode(method, writer);
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

#region sample_CreationResponse

/// <summary>
/// Base class for resource types that denote some kind of resource being created
/// in the system. Wolverine specific, and more efficient, version of Created<T> from ASP.Net Core
/// </summary>
public record CreationResponse([StringSyntax("Route")]string Url) : IHttpAware
{
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        builder.RemoveStatusCodeResponse(200);

        var create = new MethodCall(method.DeclaringType!, method).Creates.FirstOrDefault()?.VariableType;
        var metadata = new WolverineProducesResponseTypeMetadata { Type = create, StatusCode = 201 };
        builder.Metadata.Add(metadata);
    }

    void IHttpAware.Apply(HttpContext context)
    {
        context.Response.Headers.Location = Url;
        context.Response.StatusCode = 201;
    }

    public static CreationResponse<T> For<T>(T value, string url) => new CreationResponse<T>(url, value);
}

#endregion

public record CreationResponse<T>(string Url, T Value) : CreationResponse(Url);


#region sample_AcceptResponse

/// <summary>
/// Base class for resource types that denote some kind of request being accepted in the system.
/// </summary>
public record AcceptResponse(string Url) : IHttpAware
{
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        builder.RemoveStatusCodeResponse(200);

        var create = new MethodCall(method.DeclaringType!, method).Creates.FirstOrDefault()?.VariableType;
        var metadata = new WolverineProducesResponseTypeMetadata { Type = create, StatusCode = 202 };
        builder.Metadata.Add(metadata);
    }

    void IHttpAware.Apply(HttpContext context)
    {
        context.Response.Headers.Location = Url;
        context.Response.StatusCode = 202;
    }

    public static AcceptResponse<T> For<T>(T value, string url) => new AcceptResponse<T>(url, value);
}

#endregion

public record AcceptResponse<T>(string Url, T Value) : AcceptResponse(Url);