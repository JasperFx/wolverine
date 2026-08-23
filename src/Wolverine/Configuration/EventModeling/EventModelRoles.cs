using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.EventModeling;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     The non-derivable half of a slice — what the chain's <em>kind</em> knows and its handler
///     signature does not: the slice name, how the slice is triggered, the inbound command and the
///     handler type. <see cref="EventModelRoles.Describe" /> derives everything else off the chain.
/// </summary>
/// <param name="SliceName">Display name of the slice — also the merge key across sources (GH-3988).</param>
/// <param name="TriggerKind">What starts the slice — a message handler, an HTTP request, a gRPC call.</param>
/// <param name="TriggerOrigin">Structured trigger detail (route + verb, service + method), when there is one.</param>
/// <param name="CommandType">The inbound message / request type, when there is one.</param>
/// <param name="HandlerType">The handler or endpoint type that processes it.</param>
public sealed record EventModelSliceSeed(
    string SliceName,
    TriggerKind TriggerKind,
    PublisherOrigin? TriggerOrigin,
    Type? CommandType,
    Type? HandlerType)
{
    /// <summary>
    ///     The chain's response type, when it has one an HTTP resource, a gRPC response. A response is
    ///     never an emitted event or a published message, so it is excluded from the return values
    ///     <see cref="EventModelRoles.Describe" /> classifies. Null for chains that have no response concept.
    /// </summary>
    public Type? ResponseType { get; init; }

    /// <summary>
    ///     True when the first return value of the primary handler call <em>is</em> the response
    ///     (Wolverine.HTTP's convention), so that variable is skipped even when its type cannot be
    ///     named up front.
    /// </summary>
    public bool FirstReturnValueIsResponse { get; init; }

    /// <summary>
    ///     True for a read-only trigger — an HTTP <c>GET</c> / <c>HEAD</c>. A query chain that writes
    ///     nothing is a <see cref="SlicePattern.View" /> slice reading its response type as a read model.
    /// </summary>
    public bool IsQuery { get; init; }
}

/// <summary>
///     Derives a chain's Event Modeling roles — aggregates, emitted events, read models, published
///     messages, slice pattern — from what the chain already knows about itself, and writes them
///     as a JasperFx <see cref="EventModelSliceDescriptor" /> (GH-3988; jasperfx#687).
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything here is derived, never declared</b> (Spec Driven Development decision D2). The
///         roles come from two places: the chain's <see cref="IChain.Tags" /> — where the shared aggregate
///         handler workflow (<c>AggregateHandling</c>) and the DCB workflow (<c>BoundaryHandling</c>) record
///         what they loaded when the chain was customised — and the handler signature itself, so that a chain
///         whose code has not been generated yet (Dynamic code generation assembles chains lazily) still
///         reports the same roles. The two agree; the reflection walk is the one that cannot be late.
///     </para>
///     <para>
///         <b>Out of scope by decision of record:</b> imperative <c>session.Events.Append(...)</c> inside a
///         handler body is invisible at runtime — only <em>declarative</em> returns (typed events, the
///         store's <c>Events</c> collection, <c>EventsToAppend</c>, <c>IStorageAction</c>) can be read here.
///         CritterWatch's Roslyn source generator stays in place for exactly that case; it is not solved
///         here, and this class does not try to.
///     </para>
/// </remarks>
public static class EventModelRoles
{
    /// <summary>
    ///     Describe a message handler chain. The slice is named for the message type, triggered by a
    ///     message handler — or by the job scheduler for a <see cref="TimeoutMessage" /> — and the
    ///     command is the message type itself.
    /// </summary>
    public static EventModelSliceDescriptor ForHandlerChain(HandlerChain chain)
    {
        var triggerKind = chain.MessageType.CanBeCastTo<TimeoutMessage>()
            ? TriggerKind.JobScheduler
            : TriggerKind.MessageHandler;

        var handlerType = chain.Handlers.FirstOrDefault()?.HandlerType;

        return Describe(chain, new EventModelSliceSeed(
            chain.MessageType.Name,
            triggerKind,
            null,
            chain.MessageType,
            handlerType));
    }

    /// <summary>
    ///     Derive the roles for any chain — message handler, HTTP endpoint, gRPC service — from its
    ///     handler calls, its tags and the supplied <paramref name="seed" />.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Handler, message and return types come from handler discovery, which already roots them; Closes() only reads interfaces. Diagnostic surface only.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Handler, message and return types come from handler discovery, which already roots them; Closes() only reads interfaces. Diagnostic surface only.")]
    public static EventModelSliceDescriptor Describe(IChain chain, EventModelSliceSeed seed)
    {
        var roles = new RoleSet();

        readTags(chain, roles);

        foreach (var call in chain.HandlerCalls())
        {
            readSignature(chain, call, roles);
        }

        var returnTypes = returnedTypes(chain, seed).ToArray();

        // A return value that is a collection-of-events — the store's Events, EventsToAppend, any
        // IEnumerable<object> — says "this handler appends events" even though the element types
        // cannot be read off it. That is enough to classify the chain, so the other return values
        // are read as events rather than as cascaded messages.
        if (returnTypes.Any(isUntypedEventCollection))
        {
            roles.IsEventSourced = true;
        }

        foreach (var type in returnTypes)
        {
            if (isUntypedEventCollection(type)) continue;

            if (type.Closes(typeof(IStorageAction<>)))
            {
                // Store / Insert / Update / Delete of a document: the slice *produces* that read model
                roles.ReadModels.Add(type.GetGenericArguments()[0]);
                continue;
            }

            if (!isEventOrMessageCandidate(type)) continue;

            if (roles.IsEventSourced)
            {
                roles.EmittedEvents.Add(type);
            }
            else
            {
                roles.PublishedMessages.Add(type);
            }
        }

        var pattern = SlicePattern.Command;
        if (seed.IsQuery && !roles.IsEventSourced && roles.EmittedEvents.Count == 0 &&
            roles.PublishedMessages.Count == 0)
        {
            pattern = SlicePattern.View;
            if (seed.ResponseType is { } response && response != typeof(void))
            {
                roles.ReadModels.Add(response);
            }
        }

        return new EventModelSliceDescriptor(
            seed.SliceName,
            seed.TriggerOrigin?.Label,
            null,
            seed.CommandType is null ? null : TypeDescriptor.For(seed.CommandType),
            seed.HandlerType is null ? null : TypeDescriptor.For(seed.HandlerType),
            roles.EmittedEvents.Select(TypeDescriptor.For).ToList(),
            Array.Empty<TypeDescriptor>(),
            roles.ReadModels.Select(TypeDescriptor.For).ToList())
        {
            Pattern = pattern,
            TriggerKind = seed.TriggerKind,
            TriggerOrigin = seed.TriggerOrigin,
            AggregateTypes = roles.Aggregates.Select(TypeDescriptor.For).ToList(),
            PublishedMessages = roles.PublishedMessages.Select(TypeDescriptor.For).ToList(),
        };
    }

    /// <summary>
    ///     The aggregate <em>elements</em> behind a chain's <see cref="EventModelSliceDescriptor.AggregateTypes" />
    ///     — one per aggregate type, with the kind the chain uses it as and the events the type applies
    ///     (read off its conventional <c>Apply</c> / <c>Create</c> / <c>ShouldDelete</c> methods).
    /// </summary>
    public static IReadOnlyList<AggregateDescriptor> AggregatesFor(IChain chain)
    {
        var roles = new RoleSet();
        readTags(chain, roles);
        foreach (var call in chain.HandlerCalls())
        {
            readSignature(chain, call, roles);
        }

        return roles.AggregateKinds
            .Select(pair => new AggregateDescriptor(TypeDescriptor.For(pair.Key), pair.Value, AppliedEventsOf(pair.Key)))
            .ToList();
    }

    /// <summary>
    ///     The event types an aggregate applies, read off its conventional <c>Apply</c> / <c>Create</c> /
    ///     <c>ShouldDelete</c> methods (instance or static, <c>IEvent&lt;T&gt;</c> unwrapped). Empty when
    ///     the type uses none of the conventions — a hand-written <c>Evolve(IEvent)</c>, for one.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Aggregate types come from handler discovery, which already roots them. Diagnostic surface only.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Aggregate types come from handler discovery, which already roots them; Closes() only reads interfaces. Diagnostic surface only.")]
    public static IReadOnlyList<TypeDescriptor> AppliedEventsOf(Type aggregateType)
    {
        var names = new[] { "Apply", "Create", "ShouldDelete" };
        var list = new List<TypeDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in aggregateType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (!names.Contains(method.Name)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 0) continue;

            var eventType = parameters[0].ParameterType;
            if (eventType.Closes(typeof(IEvent<>)))
            {
                eventType = eventType.GetGenericArguments()[0];
            }

            if (eventType == typeof(IEvent) || eventType == typeof(object)) continue;

            var descriptor = TypeDescriptor.For(eventType);
            if (seen.Add(descriptor.FullName)) list.Add(descriptor);
        }

        return list;
    }

    private static void readTags(IChain chain, RoleSet roles)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            switch (raw)
            {
                case AggregateHandling handling:
                    roles.AddWrite(handling.AggregateType, handling.AlwaysEnforceConsistency);
                    break;
                case List<AggregateHandling> list:
                    foreach (var handling in list) roles.AddWrite(handling.AggregateType, handling.AlwaysEnforceConsistency);
                    break;
            }
        }

        if (chain.Tags.TryGetValue("BoundaryHandling", out var boundary) && boundary is BoundaryHandlingTag tag)
        {
            roles.AddBoundary(tag.AggregateType);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Handler types and their methods come from handler discovery, which already roots them. Diagnostic surface only.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Handler, message and return types come from handler discovery, which already roots them; Closes() only reads interfaces. Diagnostic surface only.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Handler, message and return types come from handler discovery, which already roots them; Closes() only reads interfaces. Diagnostic surface only.")]
    private static void readSignature(IChain chain, MethodCall call, RoleSet roles)
    {
        // [DeciderFunction] / [AggregateHandler] on the method or the handler type: the aggregate
        // is the one the attribute names, else the one the workflow would infer from the signature
        var decider = call.Method.GetAttribute<DeciderFunctionAttribute>()
                      ?? call.HandlerType.GetAttribute<DeciderFunctionAttribute>();
        if (decider != null)
        {
            var aggregateType = decider.AggregateType ?? tryDetermineAggregateType(chain);
            if (aggregateType != null) roles.AddWrite(aggregateType, decider.AlwaysEnforceConsistency);
        }

        foreach (var parameter in call.Method.GetParameters())
        {
            var parameterType = parameter.ParameterType;

            if (parameterType.Closes(typeof(IEventStream<>)))
            {
                roles.AddWrite(parameterType.GetGenericArguments()[0], false);
                continue;
            }

            if (parameter.GetAttribute<WriteModelAttribute>() is { } write)
            {
                roles.AddWrite(parameterType, write.AlwaysEnforceConsistency);
            }
            else if (parameter.HasAttribute<DcbModelAttribute>())
            {
                roles.AddBoundary(parameterType);
            }
            else if (parameter.HasAttribute<ReadModelAttribute>())
            {
                roles.AddRead(parameterType);
            }
            else if (parameter.HasAttribute<EntityAttribute>())
            {
                // A loaded entity is a read model the slice reads from
                roles.ReadModels.Add(parameterType);
            }
        }
    }

    private static Type? tryDetermineAggregateType(IChain chain)
    {
        try
        {
            return AggregateHandling.DetermineAggregateType(chain);
        }
        catch (Exception)
        {
            // Diagnostic read — a signature the workflow cannot make sense of is the workflow's
            // exception to throw at codegen time, not this reader's
            return null;
        }
    }

    private static IEnumerable<Type> returnedTypes(IChain chain, EventModelSliceSeed seed)
    {
        var calls = chain.HandlerCalls();
        for (var i = 0; i < calls.Length; i++)
        {
            var creates = calls[i].Creates.ToArray();
            for (var j = 0; j < creates.Length; j++)
            {
                if (i == 0 && j == 0 && seed.FirstReturnValueIsResponse) continue;

                var type = creates[j].VariableType;
                if (seed.ResponseType != null && type == seed.ResponseType) continue;

                yield return type;
            }
        }
    }

    private static bool isUntypedEventCollection(Type type)
    {
        if (type == typeof(IAsyncEnumerable<object>)) return true;
        if (!type.CanBeCastTo<IEnumerable<object>>()) return false;

        // OutgoingMessages is the one collection-of-objects that is explicitly NOT events
        if (type.CanBeCastTo<OutgoingMessages>()) return false;

        return true;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Handler, message and return types come from handler discovery, which already roots them; Closes() only reads interfaces. Diagnostic surface only.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Handler, message and return types come from handler discovery, which already roots them; Closes() only reads interfaces. Diagnostic surface only.")]
    private static bool isEventOrMessageCandidate(Type type)
    {
        if (type == typeof(void) || type == typeof(object) || type == typeof(object[])) return false;
        if (type == typeof(Task) || type == typeof(ValueTask)) return false;
        if (type == typeof(HandlerContinuation)) return false;
        if (type.CanBeCastTo<IWolverineReturnType>()) return false; // side effects, responses, Events, OutgoingMessages
        if (typeof(ISideEffectAware).IsAssignableFrom(type)) return false;
        if (type.CanBeCastTo<Saga>()) return false; // a saga returned from its own handler is state, not a message
        if (type.CanBeCastTo<IEnumerable<object>>()) return false;
        if (type.Closes(typeof(IAsyncEnumerable<>))) return false;

        return true;
    }

    private sealed class RoleSet
    {
        public bool IsEventSourced { get; set; }
        public List<Type> Aggregates { get; } = new();
        public Dictionary<Type, AggregateKind> AggregateKinds { get; } = new();
        public OrderedTypeSet EmittedEvents { get; } = new();
        public OrderedTypeSet PublishedMessages { get; } = new();
        public OrderedTypeSet ReadModels { get; } = new();

        public void AddWrite(Type aggregateType, bool consistent)
        {
            IsEventSourced = true;
            add(aggregateType, consistent ? AggregateKind.ConsistentAggregate : AggregateKind.WriteAggregate);
        }

        public void AddBoundary(Type modelType)
        {
            IsEventSourced = true;
            add(modelType, AggregateKind.BoundaryModel);
        }

        public void AddRead(Type aggregateType)
        {
            ReadModels.Add(aggregateType);
            if (!AggregateKinds.ContainsKey(aggregateType))
            {
                AggregateKinds[aggregateType] = AggregateKind.ReadAggregate;
            }
        }

        private void add(Type aggregateType, AggregateKind kind)
        {
            if (!Aggregates.Contains(aggregateType)) Aggregates.Add(aggregateType);

            // A write wins over a read of the same type; consistent wins over plain write
            if (!AggregateKinds.TryGetValue(aggregateType, out var existing) ||
                existing == AggregateKind.ReadAggregate ||
                (existing == AggregateKind.WriteAggregate && kind == AggregateKind.ConsistentAggregate))
            {
                AggregateKinds[aggregateType] = kind;
            }
        }
    }

    private sealed class OrderedTypeSet : IEnumerable<Type>
    {
        private readonly List<Type> _types = new();

        public int Count => _types.Count;

        public void Add(Type type)
        {
            if (!_types.Contains(type)) _types.Add(type);
        }

        public IEnumerator<Type> GetEnumerator() => _types.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
