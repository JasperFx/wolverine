using System.Timers;
using Wolverine.Configuration;
using Timer = System.Timers.Timer;

namespace Wolverine.Transports;

internal class BackPressureAgent : IDisposable
{
    private readonly IListeningAgent _agent;
    private readonly Endpoint _endpoint;
    private Timer? _timer;

    public BackPressureAgent(IListeningAgent agent, Endpoint endpoint)
    {
        _agent = agent;
        _endpoint = endpoint;
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
#pragma warning disable CS4014
#pragma warning disable VSTHRD110
        CheckNowAsync();
#pragma warning restore VSTHRD110
#pragma warning restore CS4014
    }

    public ValueTask CheckNowAsync()
    {
        if (_agent.Status is ListeningStatus.Accepting or ListeningStatus.Unknown)
        {
            if (_agent.QueueCount > _endpoint.BufferingLimits.Maximum)
            {
                return _agent.MarkAsTooBusyAndStopReceivingAsync();
            }
        }
        else if (_agent.Status == ListeningStatus.TooBusy)
        {
            if (_agent.QueueCount <= _endpoint.BufferingLimits.Restart)
            {
                return _agent.StartAsync();
            }
        }

        return ValueTask.CompletedTask;
    }
}