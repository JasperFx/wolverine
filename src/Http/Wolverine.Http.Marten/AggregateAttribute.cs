using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Http;
using Wolverine.Marten;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Publishing;
using Wolverine.Runtime;

namespace Wolverine.Http.Marten;

/// <summary>
/// Marks a parameter to an HTTP endpoint as being part of the Marten event sourcing
/// "aggregate handler" workflow
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class AggregateAttribute : HttpChainParameterAttribute
{
    public static IResult ValidateAggregateExists<T>(IEventStream<T> stream)
    {
        return stream.Aggregate == null ? Results.NotFound() : WolverineContinue.Result();
    }

    public string? RouteOrParameterName { get; }

    public AggregateAttribute()
    {
    }

    public Variable IdVariable { get; private set; }
    public Type? CommandType { get; private set; }

    /// <summary>
    /// Specify exactly the route or parameter name that has the
    /// identity for this aggregate argument
    /// </summary>
    /// <param name="routeOrParameterName"></param>
    public AggregateAttribute(string routeOrParameterName)
    {
        RouteOrParameterName = routeOrParameterName;
    }

    /// <summary>
    /// Opt into exclusive locking or optimistic checks on the aggregate stream
    /// version. Default is Optimistic
    /// </summary>
    public ConcurrencyStyle LoadStyle { get; set; } = ConcurrencyStyle.Optimistic;


    public override Variable Modify(HttpChain chain, ParameterInfo parameter, IServiceContainer container)
    {
        if (chain.Method.Method.GetParameters().Where(x => x.HasAttribute<AggregateAttribute>()).Count() > 1)
        {
            throw new InvalidOperationException(
                "It is only possible (today) to use a single [Aggregate] attribute on an HTTP handler method. Maybe use [ReadAggregate] if all you need is the projected data");
        }
        
        chain.Metadata.Produces(404);

        AggregateType = parameter.ParameterType;
        var store = container.GetInstance<IDocumentStore>();
        var idType = store.Options.Events.StreamIdentity == StreamIdentity.AsGuid ? typeof(Guid) : typeof(string);

        IdVariable = FindRouteVariable(idType, chain);
        if (IdVariable == null)
        {
            throw new InvalidOperationException(
                "Cannot determine an identity variable for this aggregate from the route arguments");
        }
        
        // Store information about the aggregate handling in the chain just in
        // case they're using LatestAggregate
        new AggregateHandling(AggregateType, IdVariable).Store(chain);

        VersionVariable = findVersionVariable(chain);
        CommandType = chain.InputType();

        var sessionCreator = MethodCall.For<OutboxedSessionFactory>(x => x.OpenSession(null!));
        chain.Middleware.Add(sessionCreator);

        chain.Middleware.Add(new EventStoreFrame());
        var loader = typeof(LoadAggregateFrame<>).CloseAndBuildAs<Frame>(this, AggregateType);
        chain.Middleware.Add(loader);

        // Use the active document session as an IQuerySession instead of creating a new one
        chain.Method.TrySetArgument(new Variable(typeof(IQuerySession), sessionCreator.ReturnVariable!.Usage));

        AggregateHandlerAttribute.DetermineEventCaptureHandling(chain, chain.Method, AggregateType);

        AggregateHandlerAttribute.ValidateMethodSignatureForEmittedEvents(chain, chain.Method, chain);

        var aggregate = AggregateHandlerAttribute.RelayAggregateToHandlerMethod(loader.Creates.Single(), chain.Method, AggregateType);

        chain.Postprocessors.Add(MethodCall.For<IDocumentSession>(x => x.SaveChangesAsync(default)));

        return aggregate;
    }

    public Variable VersionVariable { get; private set; }

    internal Variable? findVersionVariable(HttpChain chain)
    {
        if (chain.FindRouteVariable(typeof(int), "version", out var routeVariable))
        {
            return routeVariable;
        }

        if (chain.InputType() != null)
        {
            var member = AggregateHandlerAttribute.DetermineVersionMember(chain.InputType());
            if (member != null)
            {
                return new MemberAccessFrame(chain.InputType(), member, "version").Variable;
            }
        }

        return null;
    }

    internal Type AggregateType { get; set; }

    public Variable? FindRouteVariable(Type idType, HttpChain chain)
    {
        if (RouteOrParameterName.IsNotEmpty())
        {
            if (chain.FindRouteVariable(idType, RouteOrParameterName, out var variable))
            {
                return variable;
            }
        }

        if (chain.FindRouteVariable(idType, $"{AggregateType.Name.ToCamelCase()}Id", out var v2))
        {
            return v2;
        }

        if (chain.FindRouteVariable(idType, "id", out var v3))
        {
            return v3;
        }

        return null;
    }

    public static async Task<(IEventStream<T>, IResult)> FetchForExclusiveWriting<T>(Guid id, IDocumentSession session, CancellationToken cancellationToken) where T : class
    {
        var stream = await session.Events.FetchForExclusiveWriting<T>(id, cancellationToken);
        return (stream, stream.Aggregate == null ? Results.NotFound() : WolverineContinue.Result());
    }

    public static async Task<(IEventStream<T>, IResult)> FetchForWriting<T>(Guid id, IDocumentSession session, CancellationToken cancellationToken) where T : class
    {
        var stream = await session.Events.FetchForExclusiveWriting<T>(id, cancellationToken);
        return (stream, stream.Aggregate == null ? Results.NotFound() : WolverineContinue.Result());
    }

    public static async Task<(IEventStream<T>, IResult)> FetchForWriting<T>(Guid id, long version, IDocumentSession session, CancellationToken cancellationToken) where T : class
    {
        var stream = await session.Events.FetchForWriting<T>(id, version, cancellationToken);
        return (stream, stream.Aggregate == null ? Results.NotFound() : WolverineContinue.Result());
    }

    public static async Task<(IEventStream<T>, IResult)> FetchForExclusiveWriting<T>(string key, IDocumentSession session, CancellationToken cancellationToken) where T : class
    {
        var stream = await session.Events.FetchForExclusiveWriting<T>(key, cancellationToken);
        return (stream, stream.Aggregate == null ? Results.NotFound() : WolverineContinue.Result());
    }

    public static async Task<(IEventStream<T>, IResult)> FetchForWriting<T>(string key, IDocumentSession session, CancellationToken cancellationToken) where T : class
    {
        var stream = await session.Events.FetchForExclusiveWriting<T>(key, cancellationToken);
        return (stream, stream.Aggregate == null ? Results.NotFound() : WolverineContinue.Result());
    }

    public static async Task<(IEventStream<T>, IResult)> FetchForWriting<T>(string key, long version, IDocumentSession session, CancellationToken cancellationToken) where T : class
    {
        var stream = await session.Events.FetchForWriting<T>(key, version, cancellationToken);
        return (stream, stream.Aggregate == null ? Results.NotFound() : WolverineContinue.Result());
    }
}
