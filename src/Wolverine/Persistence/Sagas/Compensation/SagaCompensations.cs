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

        if (envelope != null && envelope.HasHeaders && envelope.Headers.ContainsKey(MarkFailedHeader))
        {
            record(saga, typeof(TMessage), thrownType, CompensationOutcome.Failed, SagaStatus.CompensationFailed);
            return;
        }

        var method = SagaCompensationMethods.For(typeof(TSaga)).Select(typeof(TMessage), fault.Exception, live);
        if (method == null)
        {
            context.Runtime.Logger.LogDebug(
                "No Compensate method of saga {SagaType} matches the failure of {MessageType} with {ExceptionType}, so nothing was compensated",
                typeof(TSaga).FullNameInCode(), typeof(TMessage).FullNameInCode(), thrownType);
            return;
        }

        var result = await method.InvokeAsync(saga, fault.Message, fault.Exception, live, context).ConfigureAwait(false);

        record(saga, typeof(TMessage), thrownType, CompensationOutcome.Compensated, SagaStatus.Compensated);

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

    private static void record(Saga saga, Type messageType, string exceptionType, CompensationOutcome outcome,
        SagaStatus status)
    {
        if (saga is not CompensatingSaga compensating) return;

        compensating.Status = status;
        compensating.CompensationLog ??= [];
        compensating.CompensationLog.Add(new CompensationLogEntry(messageType.ToMessageTypeName(), exceptionType,
            outcome, DateTimeOffset.UtcNow));
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
            if (methods.Any(x => x.FailedType == failedType))
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
    /// match, and saves the saga. Declaring one also asks for the fault of every T the saga sends (GH-5553)
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Fault<T> closed over the saga's own message types at bootstrap; AOT apps pre-generate handlers.")]
    internal static IEnumerable<HandlerCall> FindCalls(IEnumerable<HandlerCall> discovered)
    {
        foreach (var sagaType in discovered.Select(x => x.HandlerType).Where(x => x.CanBeCastTo<Saga>()).Distinct())
        {
            foreach (var method in SagaCompensationMethods.For(sagaType))
            {
                yield return new HandlerCall(sagaType, method.Method, typeof(Fault<>).MakeGenericType(method.FailedType));
            }
        }
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
        var fault = FaultPublisher.CreateFault(original.Message!, ExceptionInfo.From(exception), original);
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
    public async Task TryMarkFailedAsync(Envelope faultEnvelope, CancellationToken cancellation)
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
