using System.Data;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Http;
using Wolverine.Configuration;
using Wolverine.Http.CodeGen;
using Wolverine.Marten;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Publishing;

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


    public override Variable Modify(HttpChain chain, ParameterInfo parameter, IContainer container)
    {
        AggregateType = parameter.ParameterType;
        var store = container.GetInstance<IDocumentStore>();
        var idType = store.Options.Events.StreamIdentity == StreamIdentity.AsGuid ? typeof(Guid) : typeof(string);

        IdVariable = FindRouteVariable(idType, chain);
        if (IdVariable == null)
        {
            throw new InvalidOperationException(
                "Cannot determine an identity variable for this aggregate from the route arguments");
        }

        VersionVariable = findVersionVariable(chain);
        CommandType = chain.InputType();

        var sessionCreator = MethodCall.For<OutboxedSessionFactory>(x => x.OpenSession(null!));
        chain.Middleware.Add(sessionCreator);
        
        var loader = generateLoadAggregateCode(chain);

        var method = typeof(AggregateAttribute)
            .GetMethod(nameof(ValidateAggregateExists), BindingFlags.Public | BindingFlags.Static)
            .MakeGenericMethod(AggregateType);

        var assertExists = new MethodCall(typeof(AggregateAttribute), method);
        chain.Middleware.Add(assertExists);
        
        chain.Middleware.Add(new MaybeEndWithResultFrame(assertExists.ReturnVariable));
        
        // Use the active document session as an IQuerySession instead of creating a new one
        chain.Method.TrySetArgument(new Variable(typeof(IQuerySession), sessionCreator.ReturnVariable!.Usage));
        
        AggregateHandlerAttribute.DetermineEventCaptureHandling(chain, chain.Method, AggregateType);
        
        AggregateHandlerAttribute.ValidateMethodSignatureForEmittedEvents(chain, chain.Method, chain);
        
        var aggregate = AggregateHandlerAttribute.RelayAggregateToHandlerMethod(loader, chain.Method, AggregateType);
        
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
    
    private MethodCall generateLoadAggregateCode(IChain chain)
    {
        chain.Middleware.Add(new EventStoreFrame());
        var loader = typeof(LoadAggregateFrame<>).CloseAndBuildAs<MethodCall>(this, AggregateType!);
        
        
        chain.Middleware.Add(loader);
        return loader;
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
    
}

// TODO -- this should absolutely be in JasperFx.CodeGeneration
internal class MemberAccessFrame : SyncFrame
{
    private readonly Type _targetType;
    private readonly MemberInfo _member;
    private Variable _parent;
    public Variable Variable { get; }
    
    public MemberAccessFrame(Type targetType, MemberInfo member, string name)
    {
        _targetType = targetType;
        _member = member;
        Variable = new Variable(member.GetMemberType(), name, this);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Variable.Usage} = {_parent.Usage}.{_member.Name};");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _parent = chain.FindVariable(_targetType);
        yield return _parent;
    }
}

internal class LoadAggregateFrame<T> : MethodCall where T : class
{
    private readonly AggregateAttribute _att;

    public LoadAggregateFrame(AggregateAttribute att) : base(typeof(IEventStore), FindMethod(att))
    {
        _att = att;
        CommentText = "Loading Marten aggregate";
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        Arguments[0] = _att.IdVariable;
        if (_att.LoadStyle == ConcurrencyStyle.Optimistic && _att.VersionVariable != null)
        {
            Arguments[1] = _att.VersionVariable;
        }

        foreach (var variable in base.FindVariables(chain)) yield return variable;
    }

    internal static MethodInfo FindMethod(AggregateAttribute att)
    {
        var isGuidIdentified = att.IdVariable.VariableType == typeof(Guid);

        if (att.LoadStyle == ConcurrencyStyle.Exclusive)
        {
            return isGuidIdentified
                ? ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForExclusiveWriting<T>(Guid.Empty, default))!
                : ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForExclusiveWriting<T>(string.Empty, default))!;
        }

        if (att.VersionVariable == null)
        {
            return isGuidIdentified
                ? ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForWriting<T>(Guid.Empty, default))!
                : ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForWriting<T>(string.Empty, default))!;
        }

        return isGuidIdentified
            ? ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForWriting<T>(Guid.Empty, long.MaxValue, default))!
            : ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForWriting<T>(string.Empty, long.MaxValue, default))!;
    }
}

