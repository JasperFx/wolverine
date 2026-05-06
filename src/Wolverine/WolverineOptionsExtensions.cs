using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime.Heartbeat;

namespace Wolverine;

/// <summary>
/// Extension methods that opt a <see cref="WolverineOptions"/> instance into ancillary
/// runtime features without modifying the core options class.
/// </summary>
public static class WolverineOptionsExtensions
{
    /// <summary>
    /// Enable periodic <see cref="WolverineHeartbeat"/> emission from this Wolverine node.
    /// Heartbeats are routed through the normal publish pipeline, so callers must register
    /// a publish rule (e.g. <c>opts.PublishMessage&lt;WolverineHeartbeat&gt;().ToRabbitExchange("monitoring")</c>)
    /// to deliver them to an external monitoring tool such as CritterWatch. With no rule
    /// configured the heartbeat is a local-only no-op unless a local subscriber exists.
    /// </summary>
    /// <param name="opts">The <see cref="WolverineOptions"/> being configured.</param>
    /// <param name="interval">
    /// Optional override for the heartbeat cadence. When <c>null</c> the existing
    /// <see cref="HeartbeatPolicy.Interval"/> value (default 30 seconds) is preserved.
    /// </param>
    /// <returns>The same <see cref="WolverineOptions"/> instance for fluent chaining.</returns>
    public static WolverineOptions EnableHeartbeats(this WolverineOptions opts, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.Heartbeat.Enabled = true;
        if (interval.HasValue)
        {
            opts.Heartbeat.Interval = interval.Value;
        }

        opts.Services.AddSingleton<HeartbeatBackgroundService>();
        opts.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatBackgroundService>());

        return opts;
    }
}
