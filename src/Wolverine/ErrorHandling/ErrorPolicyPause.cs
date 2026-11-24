using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.ErrorHandling;

/// <summary>
/// GH-5945. Implemented by the listener circuits that can actually pause (<see cref="ListeningAgent" /> and the
/// durable local queue), so <see cref="PauseListenerContinuation" /> can pause one as an error policy's pause and
/// have both ends of it reported to <see cref="Runtime.Agents.IWolverineObserver" />.
/// </summary>
internal interface IPausesForErrorPolicy
{
    ValueTask PauseForErrorPolicyAsync(TimeSpan pauseTime, Exception? exception);
}

/// <summary>
/// GH-5945. A listener circuit's record of an error policy pause still waiting for its resume, the error policy
/// counterpart of the circuit breaker's pending reset (GH-5446). The circuit calls <see cref="ResumedAsync" /> every
/// time it becomes Accepting; only the first one after an error policy pause reaches the observer.
/// </summary>
internal sealed class ErrorPolicyPause
{
    private readonly IWolverineRuntime _runtime;
    private readonly Endpoint _endpoint;
    private readonly ILogger _logger;
    private int _awaitingResume;

    public ErrorPolicyPause(IWolverineRuntime runtime, Endpoint endpoint, ILogger logger)
    {
        _runtime = runtime;
        _endpoint = endpoint;
        _logger = logger;
    }

    internal bool IsAwaitingResume => Volatile.Read(ref _awaitingResume) == 1;

    /// <summary>
    /// Called before the circuit pauses, because the pause installs the restart: with a short pause time the listener
    /// can be accepting again before the pause call even returns, and the resume has to find this pause pending.
    /// </summary>
    internal async ValueTask PausingAsync(TimeSpan pauseTime, Exception? exception)
    {
        Volatile.Write(ref _awaitingResume, 1);

        try
        {
            await _runtime.Observer.ListenerPaused(_endpoint, pauseTime, exception);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error notifying the observer that the listener for {Uri} was paused by an error policy",
                _endpoint.Uri);
        }
    }

    /// <summary>
    /// A pause no error policy made replaced this one, so the start that ends it is not this pause's resume.
    /// </summary>
    internal void Superseded()
    {
        Volatile.Write(ref _awaitingResume, 0);
    }

    /// <summary>
    /// The circuit is Accepting. An observer that throws is logged and swallowed: the listener is already accepting,
    /// and a failed notification must not fail the start that got it there.
    /// </summary>
    internal async ValueTask ResumedAsync()
    {
        if (Interlocked.Exchange(ref _awaitingResume, 0) == 0) return;

        try
        {
            await _runtime.Observer.ListenerResumed(_endpoint);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error notifying the observer that the listener for {Uri} resumed after an error policy pause",
                _endpoint.Uri);
        }
    }
}
