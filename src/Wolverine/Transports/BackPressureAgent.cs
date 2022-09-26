using System;
using System.Threading.Tasks;
using System.Timers;
using Wolverine.Configuration;

namespace Wolverine.Transports;

internal class BackPressureAgent : IDisposable
{
    private readonly IListeningAgent _agent;
    private readonly Endpoint _endpoint;
    private Timer _timer;

    public BackPressureAgent(IListeningAgent agent, Endpoint endpoint)
    {
        _agent = agent;
        _endpoint = endpoint;
    }

    public void Dispose()
    {
        _timer.Dispose();
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
        CheckNowAsync();
#pragma warning restore CS4014
    }

    public async ValueTask CheckNowAsync()
    {
        switch (_agent.Status)
        {
            case ListeningStatus.Accepting:
            case ListeningStatus.Unknown:
                if (_agent.QueueCount > _endpoint.BufferingLimits.Maximum)
                {
                    await _agent.StopReceiving();
                }

                break;
                
            case ListeningStatus.Stopped:
                return;
                
            case ListeningStatus.TooBusy:
                if (_agent.QueueCount <= _endpoint.BufferingLimits.Restart)
                {
                    await _agent.StartAsync();
                }

                break;
        }
    }
}