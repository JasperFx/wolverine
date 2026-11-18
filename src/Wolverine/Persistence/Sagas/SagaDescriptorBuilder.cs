using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Shared helper that materialises a <see cref="SagaDescriptor"/> from the
/// handler graph + saga state type. Used by
/// <see cref="ServiceCapabilities.ReadFrom"/> when emitting host-wide
/// capabilities and by per-storage <see cref="ISagaStoreDiagnostics"/>
/// implementations when reporting the saga types they own. Single source
/// of truth so the host-wide snapshot and the per-storage view agree
/// byte-for-byte on classification.
/// </summary>
internal static class SagaDescriptorBuilder
{
    /// <summary>
    /// Build one <see cref="SagaDescriptor"/> for a single saga state
    /// type by walking every <see cref="SagaChain"/> on the graph that
    /// targets this type, classifying each chain by its handler method
    /// names, and capturing per-message saga-id binding + cascading
    /// PublishedTypes.
    /// </summary>
    /// <param name="graph">The Wolverine handler graph.</param>
    /// <param name="sagaType">Concrete <c>: Saga</c> state class.</param>
    /// <param name="storageProvider">
    /// Optional storage-provider tag (e.g. <c>"Marten"</c>); null when
    /// the caller doesn't know.
    /// </param>
    public static SagaDescriptor Build(HandlerGraph graph, Type sagaType, string? storageProvider)
    {
        var eventSourced = sagaType.CanBeCastTo<EventSourcedSaga>();
        var descriptor = new SagaDescriptor(TypeDescriptor.For(sagaType))
        {
            StorageProvider = storageProvider,
            IsEventSourced = eventSourced
        };

        var chainsForSaga = CollectSagaChains(graph)
            .Where(c => c.SagaType == sagaType && c.SagaHandlerCalls().Length != 0)
            .OrderBy(c => c.MessageType.FullNameInCode())
            .ToArray();

        // SagaIdType is consistent across every chain for one saga (Wolverine
        // would have errored at runtime if it weren't), so pull it from the
        // first chain that resolved a SagaIdMember.
        var typeSource = chainsForSaga.FirstOrDefault(c => c.SagaIdMember != null);
        if (typeSource is not null)
        {
            descriptor.SagaIdType = SagaIdMemberType(typeSource.SagaIdMember!)?.FullName;
        }

        foreach (var chain in chainsForSaga)
        {
            var role = ClassifyRole(chain);
            if (role is null) continue;

            var methods = DescribeMethods(chain, sagaType, eventSourced);

            // GH-5670: an event-sourced saga's methods return events as well as messages, and only the messages are
            // published
            var published = eventSourced
                ? methods.SelectMany(x => x.Messages.Concat(x.Scheduled)).Distinct().ToArray()
                : chain.PublishedTypes()
                    .Distinct()
                    .Select(TypeDescriptor.For)
                    .ToArray();

            descriptor.Messages.Add(new SagaMessageRole(
                TypeDescriptor.For(chain.MessageType),
                role.Value,
                chain.SagaIdMember?.Name,
                published)
            {
                IsTimeout = chain.MessageType.CanBeCastTo<TimeoutMessage>(),
                Methods = methods
            });
        }

        // GH-5670: the Compensate methods, in the order Wolverine prefers them
        foreach (var method in SagaCompensationMethods.For(sagaType).Select(x => x).OrderBy(x => x.FailedType.Name).ThenBy(x => x.Order))
        {
            descriptor.Compensations.Add(new SagaCompensationDescriptor(
                TypeDescriptor.For(method.FailedType),
                TypeDescriptor.For(method.ExceptionType),
                DescribeMethod(method.Method, SagaRole.Orchestrate, sagaType, eventSourced)));
        }

        return descriptor;
    }

    /// <summary>
    /// GH-5670. The saga methods of one chain, starting methods first, then the ones for an existing saga, then
    /// NotFound: the order a reader follows them in.
    /// </summary>
    internal static SagaMethodDescriptor[] DescribeMethods(SagaChain chain, Type sagaType, bool eventSourced)
    {
        var calls = chain.SagaHandlerCalls()
            .Where(x => x.HandlerType.CanBeCastTo<Saga>())
            .Select(x => x.Method)
            .Where(x => !x.Name.StartsWith(SagaCompensations.Compensate, StringComparison.Ordinal))
            .Distinct()
            .Select(x => (Method: x, Role: roleOf(x.Name)))
            .Where(x => x.Role.HasValue)
            .OrderBy(x => x.Role!.Value)
            .ThenBy(x => x.Method.Name, StringComparer.Ordinal);

        return calls.Select(x => DescribeMethod(x.Method, x.Role!.Value, sagaType, eventSourced)).ToArray();
    }

    private static SagaRole? roleOf(string methodName)
    {
        var name = methodName.EndsWith("Async") ? methodName[..^"Async".Length] : methodName;
        return name switch
        {
            SagaChain.StartOrHandle or SagaChain.StartsOrHandles => SagaRole.StartOrHandle,
            SagaChain.Start or SagaChain.Starts => SagaRole.Start,
            SagaChain.Orchestrate or SagaChain.Orchestrates or "Handle" or "Handles" or "Consume" or "Consumes" => SagaRole.Orchestrate,
            SagaChain.NotFound => SagaRole.NotFound,
            _ => null
        };
    }

    /// <summary>
    /// GH-5670. What one saga method returns, as far as its signature says. A NotFound method never appends events: there
    /// is no saga for them to go to, so what it returns is sent.
    /// </summary>
    internal static SagaMethodDescriptor DescribeMethod(MethodInfo method, SagaRole role, Type sagaType, bool eventSourced)
    {
        var messages = new List<Type>();
        var scheduled = new List<Type>();
        var events = new List<Type>();
        var dynamic = false;
        var appends = eventSourced && role != SagaRole.NotFound;

        foreach (var type in returnedTypes(method.ReturnType))
        {
            if (type == typeof(object) || type == typeof(OutgoingMessages) ||
                (type != typeof(string) && type.CanBeCastTo<IEnumerable<object>>()) ||
                type == typeof(IAsyncEnumerable<object>))
            {
                dynamic = true;
                continue;
            }

            // A start method of a document saga returns the saga itself, which is its state rather than a message
            if (type.CanBeCastTo<Saga>()) continue;

            if (type.CanBeCastTo<TimeoutMessage>())
            {
                scheduled.Add(type);
                continue;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ScheduledMessage<>))
            {
                scheduled.Add(type.GetGenericArguments()[0]);
                continue;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DeliveryMessage<>))
            {
                messages.Add(type.GetGenericArguments()[0]);
                continue;
            }

            // Side effects, storage actions and the like are consumed inside the chain and never routed
            if (type.CanBeCastTo<INotToBeRouted>()) continue;

            if (appends && !type.CanBeCastTo<IWolverineReturnType>())
            {
                events.Add(type);
            }
            else
            {
                messages.Add(type);
            }
        }

        return new SagaMethodDescriptor(method.Name, role,
            messages.Distinct().Select(TypeDescriptor.For).ToArray(),
            scheduled.Distinct().Select(TypeDescriptor.For).ToArray(),
            events.Distinct().Select(TypeDescriptor.For).ToArray())
        {
            ReturnsMoreAtRunTime = dynamic
        };
    }

    // The types a method returns: Task<T> and ValueTask<T> unwrapped, and each member of a returned tuple
    private static IEnumerable<Type> returnedTypes(Type returnType)
    {
        if (returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask)) yield break;

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                foreach (var inner in returnedTypes(returnType.GetGenericArguments()[0])) yield return inner;
                yield break;
            }

            if (returnType.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
            {
                foreach (var member in returnType.GetGenericArguments())
                {
                    foreach (var inner in returnedTypes(member)) yield return inner;
                }

                yield break;
            }

            if (definition == typeof(Nullable<>))
            {
                yield return returnType.GetGenericArguments()[0];
                yield break;
            }
        }

        yield return returnType;
    }

    /// <summary>
    /// Classify a single <see cref="SagaChain"/> into the role that best
    /// summarises the chain's handler method names. Mirrors the lookup
    /// <see cref="SagaChain.DetermineFrames"/> uses for code-gen so the
    /// static descriptor matches runtime behaviour. <c>StartOrHandle</c>
    /// wins over <c>Start</c> because it's the strictly-more-capable role.
    /// Returns null when the chain has no recognisable saga-handler
    /// methods (shouldn't happen in practice, but defensive).
    /// </summary>
    public static SagaRole? ClassifyRole(SagaChain chain)
    {
        // GH-5331: not chain.Handlers, which DetermineFrames clears once the chain compiles
        var methodNames = chain.SagaHandlerCalls()
            .Select(h => h.Method.Name)
            .Select(n => n.EndsWith("Async") ? n[..^"Async".Length] : n)
            .ToHashSet();

        if (methodNames.Contains(SagaChain.StartOrHandle) || methodNames.Contains(SagaChain.StartsOrHandles))
            return SagaRole.StartOrHandle;

        if (methodNames.Contains(SagaChain.Start) || methodNames.Contains(SagaChain.Starts))
            return SagaRole.Start;

        if (methodNames.Contains(SagaChain.Orchestrate) || methodNames.Contains(SagaChain.Orchestrates)
            || methodNames.Contains("Handle") || methodNames.Contains("Handles")
            || methodNames.Contains("Consume") || methodNames.Contains("Consumes"))
            return SagaRole.Orchestrate;

        if (methodNames.Contains(SagaChain.NotFound))
            return SagaRole.NotFound;

        return null;
    }

    /// <summary>
    /// Every <see cref="SagaChain"/> in the handler graph, one per
    /// (message type, saga type).
    /// </summary>
    public static IEnumerable<SagaChain> CollectSagaChains(HandlerGraph graph) => graph.SagaChains();

    /// <summary>
    /// Unwrap a saga-id MemberInfo to its declared type. Used to populate
    /// <see cref="SagaDescriptor.SagaIdType"/>.
    /// </summary>
    public static Type? SagaIdMemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => null
    };
}
