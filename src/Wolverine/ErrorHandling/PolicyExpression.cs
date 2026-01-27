using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.ErrorHandling;

/// <summary>
///     Base class for a custom continuation action for a runtime message error
/// </summary>
public abstract class UserDefinedContinuation : IContinuationSource, IContinuation
{
    protected UserDefinedContinuation(string description)
    {
        Description = description;
    }

    public abstract ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity);

    public string Description { get; }

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }
}

public static class FailureActionExtensions
{
    /// <summary>
    /// Handle the exception by potentially sending compensating actions to handle the failure case. This can
    /// match on any time that can be cast to the "T" type
    /// </summary>
    /// <param name="compensatingAction"></param>
    /// <param name="invokeResult"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IAdditionalActions CompensatingAction<T>(this IFailureActions actions, Func<T, Exception, IMessageBus, ValueTask> compensatingAction, InvokeResult? invokeResult = null)
    {
        return actions.CustomAction(async (runtime, lifecycle, ex) =>
        {
            if (lifecycle.Envelope?.Message is T message)
            {
                var bus = new MessageContext(runtime);
                await compensatingAction(message, ex, bus).ConfigureAwait(false);
                await bus.FlushOutgoingMessagesAsync().ConfigureAwait(false);
            }
        }, "Compensating messages", invokeResult);
    }
}

public interface IAdditionalActions
{
    /// <summary>
    ///     Define actions to take upon subsequent failures
    /// </summary>
    IFailureActions Then { get; }

    /// <summary>
    ///     Pause all processing for the specified time. Will also requeue the
    ///     failed message that caused this to trip off
    /// </summary>
    /// <param name="pauseTime"></param>
    IAdditionalActions AndPauseProcessing(TimeSpan pauseTime);


    /// <summary>
    ///     Perform a user defined action as well as the initial action
    /// </summary>
    /// <param name="action"></param>
    /// <param name="description"></param>
    /// <returns></returns>
    IAdditionalActions And(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action,
        string description = "User supplied");
    
    /// <summary>
    ///     Take out an additional, user-defined action upon message failures
    /// </summary>
    /// <param name="source"></param>
    /// <param name="description"></param>
    /// <param name="invokeUsage">If specified, this error action will be executed for inline message execution through IMessageBus.InvokeAsync()</param>
    /// <returns></returns>
    IAdditionalActions And(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action,
        string description, InvokeResult invokeUsage);


    /// <summary>
    ///     Perform a user defined action using the IContinuationSource approach
    ///     to determine the next action
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    IAdditionalActions And<T>() where T : IContinuationSource, new();

    /// <summary>
    ///     Perform a user defined action using the IContinuationSource approach
    ///     to determine the next action
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    IAdditionalActions And(IContinuationSource source);
}

internal class FailureActions : IAdditionalActions, IFailureActions
{
    private readonly FailureRule _rule;
    private readonly List<FailureSlot> _slots = new();

    public FailureActions(IExceptionMatch match, FailureRuleCollection parent)
    {
        _rule = new FailureRule(match);
        parent.Add(_rule);
    }

    public IFailureActions Then
    {
        get
        {
            _slots.Clear();
            return this;
        }
    }

    public IAdditionalActions AndPauseProcessing(TimeSpan pauseTime)
    {
        foreach (var slot in _slots)
        {
            slot.InsertSourceAtTop(new PauseListenerContinuation(pauseTime));
        }

        return this;
    }

    /// <summary>
    ///     Take out an additional, user-defined action upon message failures
    /// </summary>
    /// <param name="source"></param>
    /// <param name="description"></param>
    /// <returns></returns>
    public IAdditionalActions And(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action,
        string description = "User supplied")
    {
        var source = new UserDefinedContinuationSource(action, description);
        return And(source);
    }
    
    /// <summary>
    ///     Take out an additional, user-defined action upon message failures
    /// </summary>
    /// <param name="source"></param>
    /// <param name="description"></param>
    /// <param name="invokeUsage">If specified, this error action will be executed for inline message execution through IMessageBus.InvokeAsync()</param>
    /// <returns></returns>
    public IAdditionalActions And(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action,
        string description, InvokeResult invokeUsage)
    {
        var source = new UserDefinedContinuationSource(action, description)
        {
            InvokeUsage = invokeUsage
        };
        
        return And(source);
    }

    public IAdditionalActions And<T>() where T : IContinuationSource, new()
    {
        return And(new T());
    }

    public IAdditionalActions And(IContinuationSource source)
    {
        foreach (var slot in _slots) slot.AddAdditionalSource(source);

        return this;
    }

    public IAdditionalActions MoveToErrorQueue()
    {
        var slot = _rule.AddSlot(new MoveToErrorQueueSource());
        _slots.Add(slot);
        return this;
    }

    public IAdditionalActions Requeue(int maxAttempts = 3)
    {
        if (maxAttempts > 25)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts),
                "Wolverine allows a maximum of 25 attempts, see the RequeueIndefinitely() option");
        
        for (var i = 0; i < maxAttempts - 1; i++)
        {
            var slot = _rule.AddSlot(RequeueContinuation.Instance);
            _slots.Add(slot);
        }

        return this;
    }
    
    public IAdditionalActions RequeueIndefinitely()
    {
        _rule.InfiniteSource = RequeueContinuation.Instance;

        return this;
    }

    public IAdditionalActions PauseThenRequeue(TimeSpan delay)
    {
        var slot = _rule.AddSlot(new RequeueContinuation(delay));
        _slots.Add(slot);
        return this;
    }

    public IAdditionalActions CustomAction(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action, string description, InvokeResult? invokeUsage = null)
    {
        var source = new UserDefinedContinuationSource(action, description)
        {
            InvokeUsage = invokeUsage
        };

        var slot = _rule.AddSlot(source);
        _slots.Add(slot);
        return this;
    }

    public IAdditionalActions CustomActionIndefinitely(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action, string description, InvokeResult? invokeUsage = null)
    {
        var source = new UserDefinedContinuationSource(action, description)
        {
            InvokeUsage = invokeUsage
        };

        _rule.InfiniteSource = source;
        return this;
    }

    public IAdditionalActions Discard()
    {
        var slot = _rule.AddSlot(DiscardEnvelope.Instance);
        _slots.Add(slot);
        return this;
    }

    public IAdditionalActions ScheduleRetry(params TimeSpan[] delays)
    {
        if (delays.Length == 0)
        {
            throw new InvalidOperationException("You must specify at least one delay time");
        }
        
        if (delays.Length > 25)
            throw new ArgumentOutOfRangeException(nameof(delays),
                "Wolverine allows a maximum of 25 attempts, see the ScheduleRetryIndefinitely() option");

        for (var i = 0; i < delays.Length; i++)
        {
            var slot = _rule.AddSlot(new ScheduledRetryContinuation(delays[i]));
            _slots.Add(slot);
        }

        return this;
    }

    public IAdditionalActions ScheduleRetryIndefinitely(params TimeSpan[] delays)
    {
        if (delays.Length == 0)
        {
            throw new InvalidOperationException("You must specify at least one delay time");
        }

        for (var i = 0; i < delays.Length; i++)
        {
            _rule.AddSlot(new ScheduledRetryContinuation(delays[i]));
        }

        _rule.InfiniteSource = new ScheduledRetryContinuation(delays.Last());

        return this;
    }

    public IAdditionalActions RetryOnce()
    {
        var slot = _rule.AddSlot(RetryInlineContinuation.Instance);
        _slots.Add(slot);

        return this;
    }

    public IAdditionalActions RetryTimes(int attempts)
    {
        if (attempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts));
        }
        
        if (attempts > 25)
            throw new ArgumentOutOfRangeException(nameof(attempts),
                "Wolverine allows a maximum of 25 attempts, maybe see one of the indefinite requeue or reschedule policies");

        for (var i = 0; i < attempts; i++)
        {
            var slot = _rule.AddSlot(RetryInlineContinuation.Instance);
            _slots.Add(slot);
        }

        return this;
    }

    public IAdditionalActions RetryWithCooldown(params TimeSpan[] delays)
    {
        if (delays.Length == 0)
        {
            throw new InvalidOperationException("You must specify at least one delay time");
        }
        
        if (delays.Length > 25)
            throw new ArgumentOutOfRangeException(nameof(delays),
                "Wolverine allows a maximum of 25 attempts, maybe see one of the indefinite requeue or reschedule policies");


        for (var i = 0; i < delays.Length; i++)
        {
            var slot = _rule.AddSlot(new RetryInlineContinuation(delays[i]));
            _slots.Add(slot);
        }

        return this;
    }
}

internal class UserDefinedContinuationSource : IContinuationSource
{
    private readonly Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> _source;

    public UserDefinedContinuationSource(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> source,
        string description = "User supplied")
    {
        Description = description;
        _source = source;
    }

    public string Description { get; }
    public InvokeResult? InvokeUsage { get; set; }

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return new LambdaContinuation(_source, ex){InvokeUsage = InvokeUsage};
    }
}

internal class LambdaContinuation : IContinuation, IInlineContinuation
{
    private readonly Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> _action;
    private readonly Exception _exception;

    public LambdaContinuation(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action,
        Exception exception)
    {
        _action = action;
        _exception = exception;
    }

    public InvokeResult? InvokeUsage { get; set; }

    public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        return _action(runtime, lifecycle, _exception);
    }

    public async ValueTask<InvokeResult> ExecuteInlineAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity, CancellationToken cancellation)
    {
        if (InvokeUsage == null)
        {
            ExceptionDispatchInfo.Throw(_exception);
            return InvokeResult.Stop;
        }

        await _action(runtime, lifecycle, _exception);
        
        return InvokeUsage.Value;
    }
}

public interface IFailureActions
{
    /// <summary>
    ///     Immediately move the message to the error queue when the exception
    ///     caught matches this criteria
    /// </summary>
    IAdditionalActions MoveToErrorQueue();

    /// <summary>
    ///     Requeue the message back to the incoming transport, with the message being
    ///     dead lettered when the maximum number of attempts is reached
    /// </summary>
    /// <param name="maxAttempts">The maximum number of attempts to process the message. The default is 3</param>
    IAdditionalActions Requeue(int maxAttempts = 3);

    /// <summary>
    /// Requeue the message back to the incoming transport no matter how many times
    /// the message has failed. Use with caution obviously!!!!!
    /// </summary>
    /// <returns></returns>
    IAdditionalActions RequeueIndefinitely();

    /// <summary>
    ///     Discard the message without any further attempt to process the message
    /// </summary>
    IAdditionalActions Discard();


    /// <summary>
    ///     Schedule the message for additional attempts with a delay. Use this
    ///     method to effect an "exponential backoff" policy
    /// </summary>
    /// <param name="delays"></param>
    /// <exception cref="InvalidOperationException"></exception>
    IAdditionalActions ScheduleRetry(params TimeSpan[] delays);
    
    /// <summary>
    ///     Schedule the message for additional attempts with a scheduled delay. The last delay will be used indefinitively.
    ///     Use this method to effect an indefinite "exponential backoff" policy where the message is removed from the queue
    /// so other messages can proceed
    /// </summary>
    /// <param name="delays"></param>
    /// <exception cref="InvalidOperationException"></exception>
    IAdditionalActions ScheduleRetryIndefinitely(params TimeSpan[] delays);

    /// <summary>
    ///     Retry the message processing inline one time
    /// </summary>
    /// <param name="maxAttempts"></param>
    IAdditionalActions RetryOnce();

    /// <summary>
    ///     Retry the message the given number attempts without any delay
    ///     or moving the message back to the original queue
    /// </summary>
    /// <param name="attempts">The number of immediate retries allowed</param>
    IAdditionalActions RetryTimes(int attempts);

    /// <summary>
    ///     Retry message failures a define number of times with user-specified cooldown times
    ///     between events. This allows for "exponential backoff" strategies
    /// </summary>
    /// <param name="delays"></param>
    /// <param name="maxAttempts"></param>
    IAdditionalActions RetryWithCooldown(params TimeSpan[] delays);

    /// <summary>
    /// Pause the current thread for a set amount of time, then requeue the message.
    /// This was created specifically to create retries without incurring concurrency
    /// issues from subsequent messages being processed simultaneously
    /// </summary>
    /// <param name="delay"></param>
    /// <returns></returns>
    IAdditionalActions PauseThenRequeue(TimeSpan delay);
    
    /// <summary>
    ///     Take out an additional, user-defined action upon message failures
    /// </summary>
    /// <param name="source"></param>
    /// <param name="description">Diagnostic description of the failure action</param>
    /// <param name="invokeUsage">If specified, this error action will be executed for inline message execution through IMessageBus.InvokeAsync()</param>
    /// <returns></returns>
    IAdditionalActions CustomAction(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action,
        string description, InvokeResult? invokeUsage = null);

    /// <summary>
    ///     Execute a user-defined action for every attempt until the custom action decides to stop.
    ///     This allows the custom action to handle its own retry logic and termination conditions.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="description">Diagnostic description of the failure action</param>
    /// <param name="invokeUsage">If specified, this error action will be executed for inline message execution through IMessageBus.InvokeAsync()</param>
    /// <returns></returns>
    IAdditionalActions CustomActionIndefinitely(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action,
        string description, InvokeResult? invokeUsage = null);
}

public class PolicyExpression : IFailureActions
{
    private readonly FailureRuleCollection _parent;

    private IExceptionMatch _match;

    internal PolicyExpression(FailureRuleCollection parent, IExceptionMatch match)
    {
        _parent = parent;
        _match = match;
    }

    public IAdditionalActions CustomAction(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action, string description, InvokeResult? invokeUsage = null)
    {
        return new FailureActions(_match, _parent).CustomAction(action, description, invokeUsage);
    }

    public IAdditionalActions CustomActionIndefinitely(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action, string description, InvokeResult? invokeUsage = null)
    {
        return new FailureActions(_match, _parent).CustomActionIndefinitely(action, description, invokeUsage);
    }

    /// <summary>
    ///     Immediately move the message to the error queue when the exception
    ///     caught matches this criteria
    /// </summary>
    public IAdditionalActions MoveToErrorQueue()
    {
        return new FailureActions(_match, _parent).MoveToErrorQueue();
    }

    /// <summary>
    ///     Requeue the message back to the incoming transport, with the message being
    ///     dead lettered when the maximum number of attempts is reached
    /// </summary>
    /// <param name="maxAttempts">The maximum number of attempts to process the message. The default is 3</param>
    public IAdditionalActions Requeue(int maxAttempts = 3)
    {
        return new FailureActions(_match, _parent).Requeue(maxAttempts);
    }

    public IAdditionalActions RequeueIndefinitely()
    {
        return new FailureActions(_match, _parent).RequeueIndefinitely();
    }

    /// <summary>
    ///     Discard the message without any further attempt to process the message
    /// </summary>
    public IAdditionalActions Discard()
    {
        return new FailureActions(_match, _parent).Discard();
    }

    /// <summary>
    ///     Schedule the message for additional attempts with a delay. Use this
    ///     method to effect an "exponential backoff" policy
    /// </summary>
    /// <param name="delays"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public IAdditionalActions ScheduleRetry(params TimeSpan[] delays)
    {
        return new FailureActions(_match, _parent).ScheduleRetry(delays);
    }

    public IAdditionalActions ScheduleRetryIndefinitely(params TimeSpan[] delays)
    {
        return new FailureActions(_match, _parent).ScheduleRetryIndefinitely(delays);
    }

    /// <summary>
    ///     Retry the current message exactly one additional time
    /// </summary>
    /// <returns></returns>
    public IAdditionalActions RetryOnce()
    {
        return new FailureActions(_match, _parent).RetryOnce();
    }

    /// <summary>
    ///     Retry the current message up to this number of additional times
    /// </summary>
    /// <param name="attempts"></param>
    /// <returns></returns>
    public IAdditionalActions RetryTimes(int attempts)
    {
        return new FailureActions(_match, _parent).RetryTimes(attempts);
    }

    /// <summary>
    ///     Retry message failures a define number of times with user-specified cooldown times
    ///     between events. This allows for "exponential backoff" strategies
    /// </summary>
    /// <param name="delays"></param>
    /// <param name="maxAttempts"></param>
    public IAdditionalActions RetryWithCooldown(params TimeSpan[] delays)
    {
        return new FailureActions(_match, _parent).RetryWithCooldown(delays);
    }

    /// <summary>
    /// Pause the current thread to slow down processing, then requeue this message
    /// </summary>
    /// <param name="delay"></param>
    /// <returns></returns>
    public IAdditionalActions PauseThenRequeue(TimeSpan delay)
    {
        return new FailureActions(_match, _parent).PauseThenRequeue(delay);
    }

    /// <summary>
    ///     Specifies that the exception message must contain this fragment. The check is case insensitive.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public PolicyExpression AndMessageContains(string text)
    {
        _match = _match.And(new MessageContains(text));
        return this;
    }

    /// <summary>
    ///     Specifies an additional type of exception that this policy can handle.
    /// </summary>
    /// <typeparam name="TException">The type of the exception to handle.</typeparam>
    /// <returns>The PolicyBuilder instance.</returns>
    public PolicyExpression Or<TException>() where TException : Exception
    {
        _match = _match.Or(new TypeMatch<TException>());

        return this;
    }

    /// <summary>
    ///     Specifies an additional type of exception that this policy can handle with additional filters on this exception
    ///     type.
    /// </summary>
    /// <param name="exceptionPredicate">The exception predicate to filter the type of exception this policy can handle.</param>
    /// <param name="description">Optional description of the filter for diagnostic purposes</param>
    /// <returns>The PolicyBuilder instance.</returns>
    public PolicyExpression Or(Func<Exception, bool> exceptionPredicate, string description = "User supplied filter")
    {
        _match = _match.Or(new UserSupplied(exceptionPredicate, description));
        return this;
    }

    /// <summary>
    ///     Specifies an additional type of exception that this policy can handle with additional filters on this exception
    ///     type.
    /// </summary>
    /// <typeparam name="TException">The type of the exception.</typeparam>
    /// <param name="exceptionPredicate">The exception predicate to filter the type of exception this policy can handle.</param>
    /// <param name="description">Optional description of the filter for diagnostic purposes</param>
    /// <returns>The PolicyBuilder instance.</returns>
    public PolicyExpression Or<TException>(Func<TException, bool> exceptionPredicate,
        string description = "User supplied filter")
        where TException : Exception
    {
        _match = _match.Or(new UserSupplied<TException>(exceptionPredicate, description));
        return this;
    }

    /// <summary>
    ///     Specifies an additional type of exception that this policy can handle if found as an InnerException of a regular
    ///     <see cref="Exception" />, or at any level of nesting within an <see cref="AggregateException" />.
    /// </summary>
    /// <typeparam name="TException">The type of the exception to handle.</typeparam>
    /// <returns>The PolicyBuilder instance, for fluent chaining.</returns>
    public PolicyExpression OrInner<TException>() where TException : Exception
    {
        _match.Or(new InnerMatch(new TypeMatch<TException>()));
        return this;
    }

    /// <summary>
    ///     Specifies an additional type of exception that this policy can handle, with additional filters on this exception
    ///     type, if
    ///     found as an InnerException of a regular <see cref="Exception" />, or at any level of nesting within an
    ///     <see cref="AggregateException" />.
    /// </summary>
    /// <typeparam name="TException">The type of the exception to handle.</typeparam>
    /// <param name="description">Optional description of the filter for diagnostic purposes</param>
    /// <returns>The PolicyBuilder instance, for fluent chaining.</returns>
    public PolicyExpression OrInner<TException>(Func<TException, bool> exceptionPredicate,
        string description = "User supplied filter")
        where TException : Exception
    {
        _match = _match.Or(new InnerMatch(new UserSupplied<TException>(exceptionPredicate, description)));
        return this;
    }
}