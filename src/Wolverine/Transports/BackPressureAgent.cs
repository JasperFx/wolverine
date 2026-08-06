using System.Timers;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime.Agents;
using Timer = System.Timers.Timer;

namespace Wolverine.Transports;

internal class BackPressureAgent : IDisposable
{
    // At the 2 second polling interval, this logs roughly once a minute while latched
    internal const int LatchedChecksPerReminder = 30;

    private readonly IListeningAgent _agent;
    private readonly Endpoint _endpoint;
    private readonly IWolverineObserver _observer;
    private readonly ILogger _logger;
    private Timer? _timer;
    private int _latchedChecks;

    public BackPressureAgent(IListeningAgent agent, Endpoint endpoint, IWolverineObserver observer, ILogger logger)
    {
        _agent = agent;
        _endpoint = endpoint;
        _observer = observer;
        _logger = logger;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    public void Start()
    {
        _timer = new Timer
        {
            AutoReset = true, Enabled = true, Interval = 2000
        };

        _timer.Elapsed += TimerOnElapsed;
    }

    private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        _ = checkSafelyAsync();
    }

    private async Task checkSafelyAsync()
    {
        // An exception escaping CheckNowAsync from the timer used to be an unobserved ValueTask
        // fault — a listener whose restart kept throwing simply never resumed, with nothing in the
        // logs (GH CritterWatch#922). The timer keeps firing, so log and let the next interval retry.
        try
        {
            await CheckNowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Back pressure check failed for listener at {Uri} (status {Status}, local count {Count}). Will retry on the next interval",
                _endpoint.Uri, _agent.Status, _agent.QueueCount);
        }
    }

    public async ValueTask CheckNowAsync()
    {
        // Update the queue activity heuristic for stale listener detection
        if (_agent is ListeningAgent la)
        {
            la.UpdateQueueCountObservation();
        }

        // CritterWatch#942 — a terminally faulted receiver (jasperfx#506) can never make progress:
        // its QueueCount is frozen (so a latched listener never resumes) and every post from the
        // receive loop throws (so an Accepting listener receives and drops messages forever). This
        // periodic check is the one reliable place to notice and force the full teardown/rebuild
        // that RestartAsync(force) already knows how to do.
        if (_agent.ReceiverHasFaulted)
        {
            _logger.LogCritical(
                "The receiver for the listener at {Uri} has terminally faulted; forcing a full listener rebuild",
                _endpoint.Uri);
            _latchedChecks = 0;
            await _agent.RestartAsync(force: true);
            return;
        }

        if (_agent.Status is ListeningStatus.Accepting or ListeningStatus.Unknown)
        {
            _latchedChecks = 0;

            if (_agent.QueueCount > _endpoint.BufferingLimits.Maximum)
            {
                await _observer.BackPressureTriggered(_endpoint, _agent);
                await _agent.MarkAsTooBusyAndStopReceivingAsync();
            }
        }
        else if (_agent.Status == ListeningStatus.TooBusy)
        {
            if (_agent.QueueCount <= _endpoint.BufferingLimits.Restart)
            {
                _latchedChecks = 0;
                await _agent.StartAsync();
                await _observer.BackPressureLifted(_endpoint);
            }
            else if (++_latchedChecks % LatchedChecksPerReminder == 0)
            {
                // A latched listener used to log exactly one line at the moment it stopped and then
                // nothing forever — operators watching a queue grow for 40 minutes had no way to tell
                // "still draining" from "wedged" (GH CritterWatch#922). Say so, with the numbers the
                // resume decision is actually made from.
                _logger.LogWarning(
                    "Listener at {Uri} is still latched by back pressure after {Seconds:N0}s: local count {Count} has not dropped to the restart threshold {Restart}. If the count is not falling, the local queue is not draining",
                    _endpoint.Uri, _latchedChecks * 2, _agent.QueueCount, _endpoint.BufferingLimits.Restart);
            }
        }
        else
        {
            _latchedChecks = 0;
        }
    }
}
