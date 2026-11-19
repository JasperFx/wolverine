using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Util;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5556. Runs a saga's <c>Compensate</c> methods. Called by the generated code of the saga's <c>Fault&lt;T&gt;</c>
/// chain, which loads and saves the saga as any other saga handler does. Not for application code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SagaCompensations
{
    /// <summary>The method name a saga declares to compensate a failed message</summary>
    public const string Compensate = "Compensate";

    // Set on the envelope of the Fault<T> that marks a saga CompensationFailed, after the compensation gave up
    internal const string MarkFailedHeader = "wolverine.compensation.mark-failed";

    // GH-5415: what the compensation that gave up threw, on that same envelope, for an event-sourced saga's
    // SagaCompensationFailed event
    internal const string FailureReasonHeader = "wolverine.compensation.failure";

    /// <summary>
    /// Compensate the failed <typeparamref name="TMessage" /> on <paramref name="saga" />: call the
    /// <c>Compensate</c> method that matches the failure best, record it on a <see cref="CompensatingSaga" />, and
    /// cascade what the method returned. A failure no <c>Compensate</c> method matches is left alone
    /// </summary>
    public static async Task CompensateAsync<TSaga, TMessage>(TSaga saga, Fault<TMessage> fault, MessageContext context)
        where TSaga : Saga where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(saga);
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(context);

        var envelope = context.Envelope;
        var live = envelope?.CompensatingException;
        var thrownType = live?.GetType().FullName ?? fault.Exception.Type;

        // The failed message's own type, which may derive from the TMessage this chain is for
        var messageType = fault.Message.GetType();

        var step = messageType.ToMessageTypeName();
        var failedId = FailedMessageIdOf(envelope);

        if (envelope != null && envelope.HasHeaders && envelope.Headers.ContainsKey(MarkFailedHeader))
        {
            if (saga is CompensatingSaga failing)
            {
                var reason = envelope.Headers.TryGetValue(FailureReasonHeader, out var failure) && failure != null
                    ? failure
                    : "The compensation failed after its retries";
                failing.Record(new SagaCompensating(step, thrownType) { FailedMessageId = failedId });
                failing.Record(new SagaCompensationFailed(step, thrownType, reason));
            }

            return;
        }

        var method = SagaCompensationMethods.For(typeof(TSaga)).Select(messageType, fault.Exception, live);
        if (method == null)
        {
            context.Runtime.Logger.LogDebug(
                "No Compensate method of saga {SagaType} matches the failure of {MessageType} with {ExceptionType}, so nothing was compensated",
                typeof(TSaga).FullNameInCode(), messageType.FullNameInCode(), thrownType);
            return;
        }

        (saga as CompensatingSaga)?.BeginHandling();
        var result = await method.InvokeAsync(saga, fault.Message, fault.Exception, live, context).ConfigureAwait(false);

        if (saga is CompensatingSaga compensating)
        {
            // GH-5673: a sequential unwind keeps the saga Compensating until its last undo message is confirmed
            if (result is UndoSequence { IsEmpty: false } sequence)
            {
                compensating.Record(new SagaCompensating(step, thrownType)
                {
                    FailedMessageId = failedId, Sequential = true, IncludePending = sequence.IncludePending
                });
            }
            else
            {
                compensating.Record(new SagaCompensating(step, thrownType) { FailedMessageId = failedId });
                compensating.Record(new SagaCompensated(step, thrownType) { Undone = compensating.TakeUndone() });
            }
        }

        if (result != null)
        {
            await context.EnqueueCascadingAsync(result).ConfigureAwait(false);
        }

        // Test seam for "another message saves the saga while it is being compensated"
        if ((context.Runtime as WolverineRuntime)?.CompensationHooks.BeforeSave is { } beforeSave)
        {
            await beforeSave(saga).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// GH-5673. The envelope id of the message whose failure a compensation answers: named on a fault sent back by
    /// another service, or the conversation of the fault Wolverine built for the saga's own failed handler
    /// </summary>
    internal static Guid? FailedMessageIdOf(Envelope? envelope)
    {
        if (envelope == null) return null;
        if (envelope.HasHeaders && envelope.Headers.TryGetValue(FaultHeaders.OriginalId, out var raw) &&
            Guid.TryParse(raw, out var original))
        {
            return original;
        }

        return envelope.ConversationId == Guid.Empty ? null : envelope.ConversationId;
    }

    /// <summary>
    /// The failure policy changes a saga chain gets for compensation: after its retries, a saga's own handler is
    /// compensated instead of only being dead lettered, and a compensation that gives up marks the saga
    /// CompensationFailed
    /// </summary>
    internal static void ApplyTo(SagaChain chain, FailureRuleCollection rules, IWolverineRuntime runtime)
    {
        var methods = SagaCompensationMethods.For(chain.SagaType);
        if (methods.Count == 0) return;

        if (FaultMessageType(chain.MessageType) is { } failedType)
        {
            // GH-5672: the chain may be the fault of a concrete type an interface or base-type method covers
            if (methods.Any(x => x.Covers(failedType)))
            {
                var target = new CompensationTarget(chain.SagaType, failedType, runtime);
                rules.InsteadOfErrorQueue = (moveToErrorQueue, _) => new CompensationGaveUp(moveToErrorQueue, target);
            }

            return;
        }

        // The saga's own handler: only a message the saga handles once it exists, never one that only starts it
        if (!methods.Any(x => x.FailedType.IsAssignableFrom(chain.MessageType))) return;
        if (!handlesExistingSaga(chain)) return;

        // The Fault<T> chain that compensates T, if the saga declares one for this exact message type
        var compensatedType = methods.Select(x => x.FailedType).FirstOrDefault(x => x == chain.MessageType)
                              ?? methods.Select(x => x.FailedType).First(x => x.IsAssignableFrom(chain.MessageType));
        var ownTarget = new CompensationTarget(chain.SagaType, compensatedType, runtime);
        rules.InsteadOfErrorQueue = (moveToErrorQueue, _) => new CompensateSaga(moveToErrorQueue, chain, ownTarget);
    }

    private static bool handlesExistingSaga(SagaChain chain)
    {
        string[] names =
        [
            SagaChain.Orchestrate, SagaChain.Orchestrates, SagaChain.StartOrHandle, SagaChain.StartsOrHandles,
            HandlerChain.Handle, HandlerChain.Handles, HandlerChain.Consume, HandlerChain.Consumes
        ];

        return chain.SagaHandlerCalls().Any(x => !x.Method.IsStatic &&
                                                 names.Any(n => x.Method.Name == n || x.Method.Name == n + "Async"));
    }

    /// <summary>The T of a Fault&lt;T&gt;, or null for any other type</summary>
    internal static Type? FaultMessageType(Type messageType)
        => messageType.IsGenericType && messageType.GetGenericTypeDefinition() == typeof(Fault<>)
            ? messageType.GetGenericArguments()[0]
            : null;

    /// <summary>
    /// GH-5556. A <c>Compensate</c> method of a saga, filed under <c>Fault&lt;T&gt;</c> for the T it compensates.
    /// The saga's chain for <c>Fault&lt;T&gt;</c> is then the compensation chain: it loads the saga, runs the best
    /// match, and saves the saga. Declaring one also asks for the fault of every T the saga sends (GH-5553).
    /// GH-5672: <c>Fault&lt;T&gt;</c> is not covariant, so a method for an interface or a base type is also filed under
    /// the fault of every concrete message type assignable to it that the application knows about, and of every type
    /// <see cref="CompensatesAttribute" /> names
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Fault<T> closed over the saga's own message types at bootstrap; AOT apps pre-generate handlers.")]
    internal static IEnumerable<HandlerCall> FindCalls(IEnumerable<HandlerCall> discovered)
        => FindCalls(discovered, []);

    /// <inheritdoc cref="FindCalls(IEnumerable{HandlerCall})" />
    /// <param name="discovered">Every handler method discovery found</param>
    /// <param name="registered">The message types the handler graph knows besides: routed with <c>PublishMessage&lt;T&gt;()</c> or registered</param>
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Fault<T> closed over the saga's own message types at bootstrap; AOT apps pre-generate handlers.")]
    internal static IEnumerable<HandlerCall> FindCalls(IEnumerable<HandlerCall> discovered, IEnumerable<Type> registered)
    {
        var calls = discovered.ToArray();
        HashSet<Type>? known = null;

        foreach (var sagaType in calls.Select(x => x.HandlerType).Where(x => x.CanBeCastTo<Saga>()).Distinct())
        {
            // GH-5673: a compensating saga hears back about the undo messages of a sequential unwind through a chain
            // of its own
            if (sagaType.CanBeCastTo<CompensatingSaga>() && sagaType.IsConcrete())
            {
                yield return new HandlerCall(sagaType, ContinueUnwindingMethod, SagaUndo.ReplyTypeFor(sagaType));
            }

            var methods = SagaCompensationMethods.For(sagaType);
            if (methods.Count == 0) continue;

            var own = calls.Where(x => x.HandlerType == sagaType).ToArray();
            var handled = own.Select(x => x.MessageType).ToHashSet();
            var sends = sentBy(own, sagaType);

            foreach (var method in methods)
            {
                var covered = new List<Type> { method.FailedType };
                covered.AddRange(method.NamedTypes);

                if (method.CoversAFamily)
                {
                    // A catch-all covers what this saga visibly sends. Anything else covers every message type the
                    // application knows that is assignable to it
                    var candidates = method.FailedType == typeof(object)
                        ? sends
                        : known ??= knownMessageTypes(calls, registered);

                    covered.AddRange(candidates.Where(method.Covers));
                }

                foreach (var messageType in covered.Distinct())
                {
                    if (messageType != method.FailedType && !isCompensable(messageType, handled)) continue;

                    yield return new HandlerCall(sagaType, method.Method, typeof(Fault<>).MakeGenericType(messageType));
                }
            }
        }
    }

    // A concrete message class the saga does not handle itself, and whose fault it does not already handle by hand
    private static bool isCompensable(Type messageType, HashSet<Type> handledBySaga)
    {
        if (messageType.IsValueType || messageType.IsAbstract || messageType.IsInterface || messageType == typeof(string)) return false;
        if (messageType.IsGenericTypeDefinition || FaultMessageType(messageType) != null) return false;
        if (messageType.CanBeCastTo<Saga>() || messageType.CanBeCastTo<INotToBeRouted>()) return false;
        if (messageType == typeof(Wolverine.Runtime.RemoteInvocation.Acknowledgement) || messageType == typeof(object)) return false;
        if (handledBySaga.Contains(messageType)) return false;

        return !handledBySaga.Contains(typeof(Fault<>).MakeGenericType(messageType));
    }

    private static readonly MethodInfo ContinueUnwindingMethod =
        typeof(CompensatingSaga).GetMethod(nameof(CompensatingSaga.ContinueUnwinding))!;

    private static readonly Type[] _notMessageTypes =
    [
        typeof(object), typeof(object[]), typeof(IEnumerable<object>), typeof(IList<object>), typeof(IReadOnlyList<object>),
        typeof(OutgoingMessages)
    ];

    // The message types a saga's own methods return, as far as their signatures say. An event-sourced saga's returns
    // are its events, so it sends nothing anyone can see this way
    private static HashSet<Type> sentBy(IEnumerable<HandlerCall> calls, Type sagaType)
    {
        if (sagaType.CanBeCastTo<EventSourcedSaga>()) return [];

        return calls.SelectMany(returnedTypes).ToHashSet();
    }

    private static IEnumerable<Type> returnedTypes(HandlerCall call)
    {
        foreach (var variable in call.Creates)
        {
            var type = variable.VariableType;
            if (_notMessageTypes.Contains(type) || type.CanBeCastTo<System.Collections.IEnumerable>()) continue;
            if (type.CanBeCastTo<INotToBeRouted>()) continue;

            yield return type;
        }
    }

    // GH-5672: every message type the application knows of. The messages its handlers handle, those its handlers
    // (other than event-sourced sagas, whose returns are events) return, and those the handler graph knows besides
    private static HashSet<Type> knownMessageTypes(IReadOnlyList<HandlerCall> calls, IEnumerable<Type> registered)
    {
        var known = new HashSet<Type>();
        foreach (var call in calls)
        {
            known.Add(call.MessageType);
            if (call.HandlerType.CanBeCastTo<EventSourcedSaga>()) continue;

            foreach (var type in returnedTypes(call)) known.Add(type);
        }

        known.UnionWith(registered);
        return known;
    }
}

/// <summary>
/// Test seams for the compensation specifications: what another message does to a saga while it is compensated
/// </summary>
internal sealed class SagaCompensationHooks
{
    /// <summary>Runs before compensation loads the saga: the saga type and its id</summary>
    public Func<Type, object, Task>? BeforeCompensating { get; set; }

    /// <summary>Runs after a Compensate method returned, before the saga is saved</summary>
    public Func<Saga, Task>? BeforeSave { get; set; }
}

/// <summary>
/// One saga's compensation chain for one failed message type, and how to run it in this process
/// </summary>
internal sealed class CompensationTarget
{
    private readonly IWolverineRuntime _runtime;
    private IExecutor? _compensate;
    private IExecutor? _markFailed;

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Fault<T> closed over the saga's own message types at bootstrap; AOT apps pre-generate handlers.")]
    public CompensationTarget(Type sagaType, Type failedType, IWolverineRuntime runtime)
    {
        SagaType = sagaType;
        FailedType = failedType;
        FaultType = typeof(Fault<>).MakeGenericType(failedType);
        _runtime = runtime;
    }

    public Type SagaType { get; }
    public Type FailedType { get; }
    public Type FaultType { get; }

    /// <summary>Does the saga show its compensation, so a failed one is worth recording?</summary>
    public bool Records => SagaType.CanBeCastTo<CompensatingSaga>();

    private IMessageHandler handler()
    {
        var runtime = (WolverineRuntime)_runtime;
        var chain = runtime.Handlers.ChainFor(FaultType, SagaType)
                    ?? throw new InvalidOperationException(
                        $"No compensation chain for {FaultType.FullNameInCode()} on saga {SagaType.FullNameInCode()}");
        return runtime.Handlers.HandlerForChain(chain);
    }

    /// <summary>Compensate, retried inline under the compensation chain's error policies</summary>
    public IExecutor Compensation
    {
        get
        {
            if (_compensate != null) return _compensate;

            var runtime = (WolverineRuntime)_runtime;
            return _compensate = Executor.Build(runtime, runtime.ExecutionPool, runtime.Handlers, handler(),
                runtime.MessageTracking);
        }
    }

    /// <summary>Mark the saga CompensationFailed: once, with no retries</summary>
    public IExecutor MarkFailed
    {
        get
        {
            if (_markFailed != null) return _markFailed;

            var runtime = (WolverineRuntime)_runtime;
            var handler = this.handler();
            var timeout = (handler as MessageHandler)?.Chain?.DetermineMessageTimeout(runtime.Options) ??
                          TimeSpan.FromSeconds(5);
            return _markFailed = new Executor(runtime.ExecutionPool, runtime, handler, new FailureRuleCollection(),
                timeout);
        }
    }

    /// <summary>
    /// The Fault&lt;T&gt; that compensates <paramref name="original" />: the failed message, its exception, and the
    /// saga id, in an envelope of its own
    /// </summary>
    public Envelope BuildFaultEnvelope(Envelope original, Exception exception, string sagaId)
    {
        // A Fault of the type the compensation chain is for, which may be a base type of the failed message
        var fault = FaultPublisher.CreateFault(FailedType, original.Message!, ExceptionInfo.From(exception), original);
        return new Envelope(fault)
        {
            SagaId = sagaId,
            TenantId = original.TenantId,
            CorrelationId = original.CorrelationId,
            ConversationId = original.Id,
            Source = original.Source,
            CompensatingException = exception
        };
    }

    /// <summary>
    /// Record that compensation gave up: the saga's status and a failed log entry, saved once. If that fails too, the
    /// dead letter is the only record
    /// </summary>
    public async Task TryMarkFailedAsync(Envelope faultEnvelope, CancellationToken cancellation, Exception? failure = null)
    {
        if (!Records) return;

        var marker = new Envelope(faultEnvelope.Message!)
        {
            SagaId = faultEnvelope.SagaId,
            TenantId = faultEnvelope.TenantId,
            CorrelationId = faultEnvelope.CorrelationId,
            ConversationId = faultEnvelope.ConversationId,
            Source = faultEnvelope.Source,
            CompensatingException = faultEnvelope.CompensatingException
        };
        marker.Headers[SagaCompensations.MarkFailedHeader] = "true";
        if (failure != null)
        {
            marker.Headers[SagaCompensations.FailureReasonHeader] = $"{failure.GetType().Name}: {failure.Message}";
        }

        try
        {
            await MarkFailed.InvokeInlineAsync(marker, cancellation).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _runtime.Logger.LogError(e,
                "Could not mark saga {SagaType} {SagaId} as CompensationFailed; the dead letter of the failed message is the only record",
                SagaType.FullNameInCode(), faultEnvelope.SagaId);
        }
    }
}
