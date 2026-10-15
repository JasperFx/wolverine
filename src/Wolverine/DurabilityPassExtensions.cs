using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine;

public static class DurabilityPassExtensions
{
    /// <summary>
    ///     GH-5016. Run one bounded pass of durability work across every message store and return what it did.
    ///     This is how <see cref="DurabilityMode.ServerlessWithOutbox" /> recovers outgoing messages and releases
    ///     scheduled messages without any background process, triggered from outside by a scheduler, a job or a
    ///     timer-triggered function.
    /// </summary>
    /// <remarks>
    ///     A store that another pass is already working on is skipped rather than waited for. Check
    ///     <see cref="DurabilityPassReport.HasMoreWork" /> and run another pass to drain everything. Throws an
    ///     <see cref="InvalidOperationException" /> under any other durability mode, where Wolverine's own durability
    ///     agents do this work.
    /// </remarks>
    public static Task<DurabilityPassReport> RunDurabilityPassAsync(this IHost host, TimeSpan maxDuration,
        CancellationToken token = default)
    {
        return host.RunDurabilityPassAsync(new DurabilityPassOptions { MaxDuration = maxDuration }, token);
    }

    /// <summary>
    ///     GH-5016. Run one bounded pass of durability work across every message store and return what it did.
    ///     This is how <see cref="DurabilityMode.ServerlessWithOutbox" /> recovers outgoing messages and releases
    ///     scheduled messages without any background process, triggered from outside by a scheduler, a job or a
    ///     timer-triggered function.
    /// </summary>
    /// <remarks>
    ///     A store that another pass is already working on is skipped rather than waited for. Check
    ///     <see cref="DurabilityPassReport.HasMoreWork" /> and run another pass to drain everything. Throws an
    ///     <see cref="InvalidOperationException" /> under any other durability mode, where Wolverine's own durability
    ///     agents do this work.
    /// </remarks>
    public static Task<DurabilityPassReport> RunDurabilityPassAsync(this IHost host,
        DurabilityPassOptions? options = null, CancellationToken token = default)
    {
        return host.Services.GetRequiredService<IWolverineRuntime>().RunDurabilityPassAsync(options, token);
    }

    /// <summary>
    ///     GH-5016. Run one bounded pass of durability work across every message store and return what it did.
    ///     Only allowed under <see cref="DurabilityMode.ServerlessWithOutbox" />, and throws an
    ///     <see cref="InvalidOperationException" /> under any other mode
    /// </summary>
    public static Task<DurabilityPassReport> RunDurabilityPassAsync(this IWolverineRuntime runtime,
        DurabilityPassOptions? options = null, CancellationToken token = default)
    {
        return DurabilityPassRunner.For(runtime).RunAsync(options ?? new DurabilityPassOptions(), token);
    }
}
