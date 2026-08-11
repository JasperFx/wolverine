using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Polecat.Events;
using Wolverine.Configuration;
using Wolverine.Polecat.Codegen;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using System.Diagnostics.CodeAnalysis;

namespace Wolverine.Polecat;

internal record AggregateHandling(IDataRequirement Requirement)
{
    private static readonly Type _versioningBaseType = typeof(AggregateVersioning<>);

    public required Type AggregateType { get; init; }
    public required Variable AggregateId { get; init; }

    public ConcurrencyStyle LoadStyle { get; init; }
    public Variable? Version { get; init; }
    public bool AlwaysEnforceConsistency { get; init; }
    public ParameterInfo? Parameter { get; set; }
    public bool IsNaturalKey { get; init; }

    public Variable Apply(IChain chain, IServiceContainer container)
    {
        Store(chain);

        declareAggregateIdRouteParameter(chain);

        // GH-3893: the aggregate handler workflow applies transaction support on its own rather than
        // waiting for AutoApplyTransactions - PolecatPersistenceFrameProvider.CanApply deliberately
        // ignores [WriteAggregate]/[AggregateHandler] for exactly that reason. Mark the chain here too,
        // or IChain.IsTransactional reports false on a chain whose generated code ends in
        // SaveChangesAsync, and an IHttpPolicy/IChainPolicy keying on the flag gets a false negative.
        new PolecatPersistenceFrameProvider().ApplyTransactionSupport(chain, container);
        chain.IsTransactional = true;

        var loader = new LoadAggregateFrame(this);
        chain.Middleware.Add(loader);
        chain.Middleware.Add(new TagAggregateOtelFrame(AggregateType, AggregateId));

        var firstCall = chain.HandlerCalls().First();

        var eventStream = loader.Stream!;
        if (Parameter != null)
        {
            eventStream.OverrideName("stream_" + Parameter.Name);
        }

        if (AggregateType == firstCall.HandlerType)
        {
            chain.Middleware.Add(new MissingAggregateCheckFrame(AggregateType, AggregateId,
                eventStream));
        }

        new PolecatEventSourcingFrameProvider().DetermineEventCaptureHandling(chain, firstCall, AggregateType);

        ValidateMethodSignatureForEmittedEvents(chain, firstCall, chain);
        var aggregate = RelayAggregateToHandlerMethod(eventStream, chain, firstCall, AggregateType);

        if (Parameter != null && Parameter.ParameterType.Closes(typeof(IEventStream<>)))
        {
            return eventStream;
        }

        return aggregate;
    }

    /// <summary>
    /// Tell an HTTP chain the CLR type of the route parameter that names this aggregate, so that the
    /// generated OpenAPI parameter carries the identity's real schema.
    ///
    /// On the <c>[AggregateHandler]</c> shape the aggregate id is read off the command rather than off the
    /// route, so an unconstrained token — the <c>{id}</c> in <c>[WolverinePost("/orders/{id}/confirm")]</c>
    /// paired with <c>Handle(ConfirmOrder command, Order order)</c> — is bound by nothing in the endpoint
    /// signature and would otherwise be described as a plain <c>string</c>. The identity type is Polecat
    /// domain knowledge that Wolverine.Http cannot infer on its own. See GH-3420.
    /// </summary>
    private void declareAggregateIdRouteParameter(IChain chain)
    {
        if (chain is not IRoutedChain routed) return;

        var routeParameterNames = routed.RouteParameterNames;
        if (routeParameterNames.Count == 0) return;

        // Same precedence as WriteAggregateAttribute.FindIdentity()
        string?[] candidates =
        [
            (Requirement as WriteAggregateAttribute)?.RouteOrParameterName,
            $"{AggregateType.Name.ToCamelCase()}Id",
            "id"
        ];

        foreach (var candidate in candidates)
        {
            if (candidate.IsEmpty()) continue;

            var match = routeParameterNames.FirstOrDefault(x => x.EqualsIgnoreCase(candidate!));
            if (match != null)
            {
                routed.DeclareRouteParameterType(match, AggregateId.VariableType);
                return;
            }
        }
    }

    public void Store(IChain chain)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            if (raw is AggregateHandling handling)
            {
                if (ReferenceEquals(handling, this)) return;
                chain.Tags[nameof(AggregateHandling)] = new List<AggregateHandling> { handling, this };
            }
            else if (raw is List<AggregateHandling> list)
            {
                list.Add(this);
            }
        }
        else
        {
            chain.Tags[nameof(AggregateHandling)] = this;
        }
    }

    public static bool TryLoad(IChain chain, [NotNullWhen(true)] out AggregateHandling? handling)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            if (raw is AggregateHandling h)
            {
                handling = h;
                return true;
            }
        }

        handling = default;
        return false;
    }

    public static bool TryLoad<T>(IChain chain, [NotNullWhen(true)] out AggregateHandling? handling)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            if (raw is AggregateHandling h && h.AggregateType == typeof(T))
            {
                handling = h;
                return true;
            }

            if (raw is List<AggregateHandling> list)
            {
                handling = list.FirstOrDefault(x => x.AggregateType == typeof(T));
                return handling != null;
            }
        }

        handling = default;
        return false;
    }

    internal static (MemberInfo, MemberInfo?) DetermineAggregateIdAndVersion(Type aggregateType, Type commandType,
        IServiceContainer container, string? versionSource = null)
    {
        if (commandType.Closes(typeof(IEvent<>)))
        {
            var concreteEventType = typeof(Event<>).MakeGenericType(commandType.GetGenericArguments()[0]);

            // This CANNOT work if you capture the version, because there's no way to know if the aggregate version
            // has advanced
            //VersionMember = concreteEventType.GetProperty(nameof(IEvent.Version));

            var options = container.Services.GetRequiredService<StoreOptions>();
            var flattenHierarchy = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var member = options.Events.StreamIdentity == StreamIdentity.AsGuid
                ? concreteEventType.GetProperty(nameof(IEvent.StreamId), flattenHierarchy)
                : concreteEventType.GetProperty(nameof(IEvent.StreamKey), flattenHierarchy);

            return (member!, null);
        }

        var aggregateId = DetermineAggregateIdMember(aggregateType, commandType);
        var version = versionSource != null
            ? DetermineVersionMemberByName(commandType, versionSource)
            : DetermineVersionMember(commandType);
        return (aggregateId, version);
    }

    internal static MemberInfo? DetermineVersionMemberByName(Type commandType, string memberName)
    {
        var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var prop = commandType.GetProperties(bindingFlags)
            .FirstOrDefault(x => x.Name.EqualsIgnoreCase(memberName)
                                 && (x.PropertyType == typeof(int) || x.PropertyType == typeof(long)));

        if (prop != null) return prop;

        var field = commandType.GetFields(bindingFlags)
            .FirstOrDefault(x => x.Name.EqualsIgnoreCase(memberName)
                                 && (x.FieldType == typeof(int) || x.FieldType == typeof(long)));

        return field;
    }

    internal static void ValidateMethodSignatureForEmittedEvents(IChain chain, MethodCall firstCall,
        IChain handlerChain)
    {
        if (firstCall.Method.ReturnType == typeof(Task) || firstCall.Method.ReturnType == typeof(void))
        {
            var parameters = chain.HandlerCalls().First().Method.GetParameters();
            var stream = parameters.FirstOrDefault(x => x.ParameterType.Closes(typeof(IEventStream<>)));
            if (stream == null)
            {
                throw new InvalidOperationException(
                    $"No events are emitted from handler {handlerChain} even though it is marked as an action that would emit Polecat events. Either return the events from the handler, or use the IEventStream<T> service as an argument.");
            }
        }
    }

    internal static MemberInfo DetermineAggregateIdMember(Type aggregateType, Type commandType)
    {
        var conventionalMemberName = $"{aggregateType.Name}Id";

        // Honor both the Polecat-specific [Identity] and the shared JasperFx.IdentityAttribute
        // used across the rest of the Critter Stack (and Marten) so a single store-agnostic
        // command/aggregate source can codegen against both stores.
        var member = commandType.GetMembers().FirstOrDefault(x =>
                         x.HasAttribute<IdentityAttribute>() || x.HasAttribute<JasperFx.IdentityAttribute>())
                     ?? commandType.GetMembers().FirstOrDefault(x =>
                         x.Name.EqualsIgnoreCase(conventionalMemberName) || x.Name.EqualsIgnoreCase("Id"));

        if (member == null)
        {
            // Fall back: if the aggregate uses a strong typed ID, look for a single
            // property of that exact type on the command
            member = TryFindStrongTypedIdMember(aggregateType, commandType);
        }

        if (member == null)
        {
            throw new InvalidOperationException(
                $"Unable to determine the aggregate id for aggregate type {aggregateType.FullNameInCode()} on command type {commandType.FullNameInCode()}. Either make a property or field named '{conventionalMemberName}', or decorate a member with the {typeof(IdentityAttribute).FullNameInCode()} or {typeof(JasperFx.IdentityAttribute).FullNameInCode()} attribute");
        }

        return member;
    }

    internal static MemberInfo? TryFindStrongTypedIdMember(Type aggregateType, Type commandType)
    {
        // Determine the strong typed ID type from the aggregate
        var strongTypedIdType = WriteAggregateAttribute.FindIdentifiedByType(aggregateType);

        if (strongTypedIdType == null)
        {
            // Check the Id property on the aggregate itself
            var idProp = aggregateType.GetProperty("Id");
            if (idProp != null && !WriteAggregateAttribute.IsPrimitiveIdType(idProp.PropertyType))
            {
                strongTypedIdType = idProp.PropertyType;
            }
        }

        if (strongTypedIdType == null) return null;

        // Look for a single property of the strong typed ID type on the command
        var matchingProps = commandType.GetProperties()
            .Where(x => x.PropertyType == strongTypedIdType && x.CanRead)
            .ToArray();

        return matchingProps.Length == 1 ? matchingProps[0] : null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2065",
        Justification = "MakeGenericType closes IEventStream<TAggregate>; GetProperty(nameof(IEventStream.Aggregate)) is statically referenced via nameof and the closed-generic IEventStream<TAggregate> preserves the Aggregate property by virtue of being instantiated by codegen. AOT consumers pre-generate via TypeLoadMode.Static.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType closes IEventStream<TAggregate> at codegen time; AOT consumers pre-generate via TypeLoadMode.Static.")]
    internal Variable RelayAggregateToHandlerMethod(Variable eventStream, IChain chain, MethodCall firstCall,
        Type aggregateType)
    {
        var member = typeof(IEventStream<>).MakeGenericType(aggregateType).GetProperty(nameof(IEventStream<string>.Aggregate));
        Variable aggregateVariable = new MemberAccessVariable(eventStream, member!);

        if (Requirement.Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(aggregateVariable, AggregateId, Requirement);
            var block = new LoadEntityFrameBlock(aggregateVariable, otherFrames);
            block.AlsoMirrorAsTheCreator(eventStream);
            chain.Middleware.Add(block);
            aggregateVariable = block.Mirror;
        }

        if (firstCall.HandlerType == aggregateType)
        {
            // If the handle method is on the aggregate itself
            firstCall.Target = aggregateVariable;
        }
        else if (Parameter != null && Parameter.ParameterType.Closes(typeof(IEventStream<>)))
        {
            // When the handler parameter is IEventStream<T>, set the stream directly by name
            var index = Array.FindIndex(firstCall.Method.GetParameters(), x => x.Name == Parameter.Name);
            firstCall.Arguments[index] = eventStream;
        }
        else if (Parameter != null)
        {
            // Use name-based matching to avoid accidentally setting the wrong same-type parameter
            firstCall.TrySetArgument(Parameter.Name!, aggregateVariable);
        }
        else
        {
            firstCall.TrySetArgument(aggregateVariable);
        }

        // Store deferred assignment for middleware methods added later (Before/After)
        if (Parameter != null)
        {
            StoreDeferredMiddlewareVariable(chain, Parameter.Name!, aggregateVariable);
        }

        // Also do immediate relay for any middleware already present
        foreach (var methodCall in chain.Middleware.OfType<MethodCall>())
        {
            if (Parameter != null)
            {
                if (!methodCall.TrySetArgument(Parameter.Name!, aggregateVariable))
                {
                    methodCall.TrySetArgument(aggregateVariable);
                }
            }
            else
            {
                methodCall.TrySetArgument(aggregateVariable);
            }
        }

        return aggregateVariable;
    }

    internal static Type DetermineAggregateType(IChain chain)
    {
        var firstCall = chain.HandlerCalls().First();
        var parameters = firstCall.Method.GetParameters();
        var stream = parameters.FirstOrDefault(x => x.ParameterType.Closes(typeof(IEventStream<>)));
        if (stream != null)
        {
            return stream.ParameterType.GetGenericArguments().Single();
        }

        if (parameters.Length >= 2 && (parameters[1].ParameterType.IsConcrete() ||
                                       parameters[1].ParameterType.Closes(typeof(IEvent<>))))
        {
            return parameters[1].ParameterType;
        }

        // Assume that the handler type itself is the aggregate
        if (firstCall.HandlerType.HasAttribute<AggregateHandlerAttribute>())
        {
            return firstCall.HandlerType;
        }

        throw new InvalidOperationException(
            $"Unable to determine a Polecat aggregate type for {chain}. You may need to explicitly specify the aggregate type in a {nameof(AggregateHandlerAttribute)} attribute");
    }

    internal static MemberInfo? DetermineVersionMember(Type aggregateType)
    {
        // The first arg doesn't matter
        var versioning =
            _versioningBaseType.CloseAndBuildAs<IAggregateVersioning>(AggregationScope.SingleStream, aggregateType);
        return versioning.VersionMember;
    }

    internal static void StoreDeferredMiddlewareVariable(IChain chain, string parameterName, Variable variable)
    {
        const string key = "DeferredMiddlewareVariables";
        if (!chain.Tags.TryGetValue(key, out var raw))
        {
            raw = new List<(string Name, Variable Variable)>();
            chain.Tags[key] = raw;
        }
        ((List<(string Name, Variable Variable)>)raw).Add((parameterName, variable));
    }
}
