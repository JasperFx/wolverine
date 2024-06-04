using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Lamar;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http.Policies;

namespace Wolverine.Http.Marten;

/// <summary>
/// Marks a parameter to an HTTP endpoint as being loaded as a Marten
/// document identified by a route argument. If the route argument
/// is not specified, this would look for either "typeNameId" or "id"
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class DocumentAttribute : HttpChainParameterAttribute
{
    public string? RouteArgumentName { get; }

    public DocumentAttribute()
    {
    }

    public DocumentAttribute(string routeArgumentName)
    {
        RouteArgumentName = routeArgumentName;
    }

    /// <summary>
    /// Should the absence of this document cause the endpoint to return a 404 Not Found response?
    /// Default -- for backward compatibility -- is false.
    /// </summary>
    public bool Required { get; set; } = false;

    /// <summary>
    /// If the document is soft-deleted, whether the endpoint should receive the document (<c>true</c>) or NULL (<c>false</c>).
    /// Set it to <c>false</c> and combine it with <see cref="Required"/> so a 404 will be returned for soft-deleted documents.
    /// </summary>
    public bool MaybeSoftDeleted { get; set; } = true;

    public override Variable Modify(HttpChain chain, ParameterInfo parameter, IContainer container)
    {
        chain.Metadata.Produces(404);

        var store = container.GetInstance<IDocumentStore>();
        var documentType = parameter.ParameterType;
        var mapping = store.Options.FindOrResolveDocumentType(documentType);
        var idType = mapping.IdType;

        var argument = FindRouteVariable(idType, documentType, chain);

        var loader = typeof(IQuerySession).GetMethods()
            .FirstOrDefault(x => x.Name == nameof(IDocumentSession.LoadAsync) && x.GetParameters()[0].ParameterType == idType);

        var load = new MethodCall(typeof(IDocumentSession), loader.MakeGenericMethod(documentType));
        load.Arguments[0] = argument;

        chain.Middleware.Add(load);

        if (MaybeSoftDeleted is false && mapping.Metadata.IsSoftDeleted.Enabled)
        {
            var frame = new SetVariableToNullIfSoftDeletedFrame(parameter.ParameterType);
            chain.Middleware.Add(frame);
        }
        
        if (Required)
        {
            var frame = new SetStatusCodeAndReturnIfEntityIsNullFrame(parameter.ParameterType);
            chain.Middleware.Add(frame);
        }
        
        return load.ReturnVariable;
    }

    public Variable? FindRouteVariable(Type idType, Type documentType, HttpChain chain)
    {
        if (RouteArgumentName.IsNotEmpty())
        {
            if (chain.FindRouteVariable(idType, RouteArgumentName, out var variable))
            {
                return variable;
            }
        }

        if (chain.FindRouteVariable(idType, $"{documentType.Name.ToCamelCase()}Id", out var v2))
        {
            return v2;
        }

        if (chain.FindRouteVariable(idType, "id", out var v3))
        {
            return v3;
        }

        return null;
    }
}