using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Tracking;

internal partial class TrackedSession
{
    internal TrackedSession(TrackedSession parent,
        Func<IWolverineRuntime, IMessageContext, CancellationToken, Task> func)
    {
        _stopwatch = parent._stopwatch;
        _primaryHost = parent._primaryHost;
        _otherHosts.AddRange(parent._otherHosts);
        _primaryLogger = parent._primaryLogger;
        _ignoreMessageRules.AddRange(parent._ignoreMessageRules);
        _source = new();

        AssertAnyFailureAcknowledgements = parent.AssertAnyFailureAcknowledgements;
        AssertNoExceptions = false;
        AlwaysTrackExternalTransports = parent.AlwaysTrackExternalTransports;

        Execution = c => func(_primaryLogger, c, _cancellation.Token);
    }
    
    internal async Task executeStageAsync(Func<IMessageContext, Task> execution)
    {
        await using var scope = _primaryHost.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IMessageContext>();
        await execution(context).WaitAsync(Timeout);
    }
    
    public async Task ExecuteAndTrackAsync()
    {
        setActiveSession(this);
        
        foreach (var before in Befores)
        {
            await before(_primaryLogger, _cancellation.Token);
        }

        _stopwatch.Start();

        try
        {
            await executeStageAsync(Execution);
            _executionComplete = true;
        }
        catch (TimeoutException e)
        {
            cleanUp();

            var message =
                BuildActivityMessage($"This {nameof(TrackedSession)} timed out before all activity completed.");

            throw new TimeoutException(message, e);
        }
        catch (Exception)
        {
            cleanUp();
            throw;
        }

        // This is for race conditions if the activity manages to finish really fast
        if (IsCompleted())
        {
            Status = TrackingStatus.Completed;
        }
        else
        {
            startTimeoutTracking();
            await _source.Task;
        }

        while (SecondaryStages.Any())
        {
            var child = SecondaryStages.Dequeue();
            await child.ExecuteAsync();
        }
        
        cleanUp();

        if (AssertNoExceptions)
        {
            AssertNoExceptionsWereThrown();
        }

        if (AssertAnyFailureAcknowledgements)
        {
            AssertNoFailureAcksWereSent();
        }

        if (AssertNoExceptions)
        {
            AssertNotTimedOut();
        }
    }

    internal interface ISecondStateExecution
    {
        Task ExecuteAsync();
    }

    internal class SecondaryAction : ISecondStateExecution
    {
        private readonly TrackedSession _parent;
        private readonly Func<IWolverineRuntime, CancellationToken, Task> _func;

        public SecondaryAction(TrackedSession parent, Func<IWolverineRuntime, CancellationToken, Task> func)
        {
            _parent = parent;
            _func = func;
        }

        public async Task ExecuteAsync()
        {
            await _func(_parent._primaryLogger, _parent._cancellation.Token);
        }
    }

    internal class SecondaryStage : ISecondStateExecution
    {
        private readonly TrackedSession _parent;
        private readonly Func<IWolverineRuntime, IMessageContext, CancellationToken, Task> _func;

        public SecondaryStage(TrackedSession parent, Func<IWolverineRuntime, IMessageContext, CancellationToken, Task> func)
        {
            _parent = parent;
            _func = func;
        }

        public async Task ExecuteAsync()
        {
            var child = new TrackedSession(_parent, _func);
            await child.ExecuteAndTrackAsync();

            // Copy in children data
            foreach (var envelopeHistory in child._envelopes)
            {
                _parent._envelopes[envelopeHistory.EnvelopeId] = envelopeHistory;
            }
        }
    }
}