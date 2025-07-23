using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Http.Marten;

/// <summary>
///     Marks a parameter to an HTTP endpoint as being loaded as a Marten
///     document identified by a route argument. If the route argument
///     is not specified, this would look for either "typeNameId" or "id"
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class DocumentAttribute : HttpChainParameterAttribute, IDataRequirement
{
    public DocumentAttribute()
    {
        ValueSource = ValueSource.Anything;
    }

    public DocumentAttribute(string routeArgumentName) : this()
    {
        ArgumentName = routeArgumentName;
        RouteArgumentName = routeArgumentName;
    }

    [Obsolete("Prefer the more generic ArgumentName")]
    public string? RouteArgumentName { get; }

    /// <summary>
    ///     If the document is soft-deleted, whether the endpoint should receive the document (<c>true</c>) or NULL (
    ///     <c>false</c>).
    ///     Set it to <c>false</c> and combine it with <see cref="Required" /> so a 404 will be returned for soft-deleted
    ///     documents.
    /// </summary>
    public bool MaybeSoftDeleted { get; set; } = true;

    /// <summary>
    ///     Should the absence of this document cause the endpoint to return a 404 Not Found response?
    ///     Default is <c>true</c>.
    /// </summary>
    public bool Required { get; set; } = true;

    public string? NotFoundMessage { get; set; }
    public MissingDataBehavior? MissingBehavior { get; set; }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        if (!rules.TryFindPersistenceFrameProvider(container, parameter.ParameterType, out var provider))
        {
            throw new InvalidOperationException("Could not determine a matching persistence service for entity " +
                                                parameter.ParameterType.FullNameInCode());
        }

        // I know it's goofy that this refers to the saga, but it should work fine here too
        var idType = provider.DetermineSagaIdType(parameter.ParameterType, container);

        if (!tryFindIdentityVariable(chain, parameter, idType, out var identity))
        {
            throw new InvalidEntityLoadUsageException(this, parameter);
        }

        if (identity.Creator != null)
        {
            chain.Middleware.Add(identity.Creator);
        }

        var frame = provider.DetermineLoadFrame(container, parameter.ParameterType, identity);

        var entity = frame.Creates.First(x => x.VariableType == parameter.ParameterType);

        if (Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(entity, identity, this);

            var block = new LoadEntityFrameBlock(entity, otherFrames);
            chain.Middleware.Add(block);

            return block.Mirror;
        }

        chain.Middleware.Add(frame);
        return entity;
    }

    public override Variable Modify(HttpChain chain, ParameterInfo parameter, IServiceContainer container)
    {
        // TODO -- watch this!!!!!
        chain.Metadata.Produces(404);

        var store = container.GetInstance<IDocumentStore>();
        var documentType = parameter.ParameterType;
        var mapping = store.Options.FindOrResolveDocumentType(documentType);
        var idType = mapping.IdType;

        if (!tryFindIdentityVariable(chain, parameter, idType, out var argument))
        {
            throw new InvalidEntityLoadUsageException(this, parameter);
        }

        var loader = typeof(IQuerySession).GetMethods()
            .FirstOrDefault(x =>
                x.Name == nameof(IDocumentSession.LoadAsync) && x.GetParameters()[0].ParameterType == idType);

        var load = new MethodCall(typeof(IDocumentSession), loader.MakeGenericMethod(documentType))
        {
            Arguments =
            {
                [0] = argument
            }
        };

        var entity = load.ReturnVariable;

        chain.Middleware.Add(load);

        if (MaybeSoftDeleted is false && mapping.Metadata.IsSoftDeleted.Enabled)
        {
            var frame = new SetVariableToNullIfSoftDeletedFrame(parameter.ParameterType);
            chain.Middleware.Add(frame);
        }

        if (Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(entity, argument, this);

            var block = new LoadEntityFrameBlock(entity, otherFrames);
            chain.Middleware.Add(block);

            return block.Mirror;
        }

        return entity;
    }
}