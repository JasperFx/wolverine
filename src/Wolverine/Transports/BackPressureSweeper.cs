using System.Collections.Immutable;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Transports;

/// <summary>
/// GH-4321: one shared 2-second sweep over every back-pressure-enforcing listener. Each
/// <see cref="BackPressureAgent"/> used to own a <c>System.Timers.Timer</c> — one timer-queue
/// entry, an <c>ElapsedEventArgs</c>, an async state machine, and a fire-and-forget Task per
/// endpoint per tick, forever, even on an idle process. One <see cref="PeriodicTimer"/> walking
/// a copy-on-write list does the same work with a single timer wheel entry.
/// </summary>
internal class BackPressureSweeper
{
    internal static readonly TimeSpan Interval = 2.Seconds();

    private readonly CancellationToken _cancellation;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private ImmutableArray<BackPressureAgent> _agents = ImmutableArray<BackPressureAgent>.Empty;
    private Task? _loop;

    public BackPressureSweeper(ILogger logger, CancellationToken cancellation)
    {
        _logger = logger;
        _cancellation = cancellation;
    }

    public void Register(BackPressureAgent agent)
    {
        lock (_lock)
        {
            if (!_agents.Contains(agent))
            {
                _agents = _agents.Add(agent);
            }

            // The loop only exists once something is actually listening with back pressure enabled
            _loop ??= Task.Run(runAsync, _cancellation);
        }
    }

    public void Unregister(BackPressureAgent agent)
    {
        lock (_lock)
        {
            _agents = _agents.Remove(agent);
        }
    }

    private async Task runAsync()
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(_cancellation))
            {
                var snapshot = _agents;
                foreach (var agent in snapshot)
                {
                    // CheckSafelyAsync contains its own per-agent try/catch (GH CritterWatch#922);
                    // this outer guard only protects the loop itself
                    try
                    {
                        await agent.CheckSafelyAsync();
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Unexpected error in the back pressure sweep");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }
}
