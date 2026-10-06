using System.Diagnostics;
using System.Runtime.CompilerServices;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Bobcat;

/// <summary>
/// What a tracked act does to a cold Wolverine host before its session starts the clock.
/// </summary>
public enum AutomaticWarmUp
{
    /// <summary>Nothing — every handler compiles on first use, inside whatever window is open.</summary>
    Off,

    /// <summary>
    /// Compile one handler chain, which pays the one-time cost of starting the compiler. The
    /// default: it is most of the first act's cold cost, and it is paid once however many
    /// handlers the application has.
    /// </summary>
    PrimeCompiler,

    /// <summary>
    /// Compile every handler chain. Leaves nothing to compile inside any act, at a cost that grows
    /// with the number of handlers — including ones the suite never reaches.
    /// </summary>
    AllHandlers,
}

/// <summary>
/// What warming a Wolverine host's message handlers did: how many chains were compiled (or were
/// already compiled), which were skipped because they only have endpoint-specific ("sticky")
/// handlers, whether every chain was covered, and how long it took.
/// </summary>
public sealed record HandlerWarmUpReport(
    int Warmed,
    IReadOnlyList<Type> SkippedStickyOnly,
    bool AllHandlers,
    TimeSpan Elapsed);

/// <summary>
/// One or more message handler chains failed to compile while the host was being warmed.
/// </summary>
public sealed class HandlerWarmUpException : Exception
{
    public HandlerWarmUpException(IReadOnlyList<(Type MessageType, Exception Error)> failures)
        : base(describe(failures),
            failures.Count == 1 ? failures[0].Error : new AggregateException(failures.Select(f => f.Error)))
    {
        Failures = failures;
    }

    public IReadOnlyList<(Type MessageType, Exception Error)> Failures { get; }

    private static string describe(IReadOnlyList<(Type MessageType, Exception Error)> failures)
        => $"Wolverine could not compile {failures.Count} message handler(s) while Bobcat was warming the host. "
           + "Wolverine would fail these the first time a message reached them; warming only moves the "
           + "failure to where it is visible:"
           + string.Concat(failures.Select(f =>
               $"{Environment.NewLine}  {f.MessageType.FullName}: {f.Error.GetType().Name}: {f.Error.Message}"));
}

/// <summary>
/// Moves Wolverine's first-use handler compilation out of a tracked session's timeout window
/// (bobcat#287).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> Under Wolverine's default <c>TypeLoadMode.Dynamic</c> a handler chain is
/// generated and compiled the first time a message reaches it. The host is already started by
/// then, so on a cold host the compile lands <em>inside</em> the first tracked act's window, against
/// a timeout chosen for a warm one. Measured locally: ~0.5s for the first act against ~1ms for
/// every later one; throttled to background QoS, ~1.9s against ~5ms; on a loaded CI runner, 7.98s
/// against the 5s default — reported as a downstream <c>Then</c> failure, not as a warm-up.
/// </para>
/// <para>
/// <b>Automatic, once per host, priming by default.</b> Every Bobcat step-context helper that opens
/// a tracked session (<see cref="WolverineStepContextExtensions"/>, and so every shipped Critter
/// Stack act) warms the host first, before the session is configured. The default,
/// <see cref="AutomaticWarmUp.PrimeCompiler"/>, compiles a single chain: starting the compiler is
/// the bulk of the cold cost and is paid once, whereas compiling every chain costs extra for each
/// handler the application has — including handlers the suite never reaches — and on a large
/// application that is worse than the problem. What remains for the act is a small per-chain
/// compile for the handlers it actually touches. A host is warmed once, keyed by its
/// <see cref="IWolverineRuntime"/>, so a restarted host is warmed again.
/// </para>
/// <para>
/// <b>To pay it before any scenario,</b> register <see cref="WarmUpWolverineHandlers"/> as a global
/// action (or call <see cref="WarmUpHandlers"/>): it compiles every chain after the resources start
/// and before the first feature, so no scenario's timings include codegen.
/// </para>
/// <para>
/// <b>Failures are not swallowed.</b> A chain that cannot compile is a handler Wolverine would have
/// failed on first use — and under <c>TypeLoadMode.Static</c>, at startup. Warming reports every
/// such chain in one <see cref="HandlerWarmUpException"/>, and the outcome is remembered so later
/// acts on that host fail the same way rather than retrying the compile.
/// </para>
/// <para>
/// <b>HTTP endpoints are not covered.</b> A Wolverine.HTTP route compiles on its first request,
/// and WolverineFx.Bobcat has no Wolverine.HTTP reference to reach it. Wolverine already owns the
/// switch: <c>app.MapWolverineEndpoints(opts =&gt; opts.WarmUpRoutes = RouteWarmup.Eager)</c>
/// builds every route while the host starts. See <c>docs/integrations/wolverine.md</c>.
/// </para>
/// </remarks>
public static class HandlerWarmUp
{
    private sealed class Outcome
    {
        public HandlerWarmUpReport? Report;
        public HandlerWarmUpException? Failure;
    }

    private static readonly ConditionalWeakTable<IWolverineRuntime, Outcome> outcomes = new();
    private static readonly object gate = new();

    /// <summary>
    /// What a tracked act does to a cold host before its session starts. Default
    /// <see cref="AutomaticWarmUp.PrimeCompiler"/>. Process-wide.
    /// </summary>
    public static AutomaticWarmUp Automatic { get; set; } = AutomaticWarmUp.PrimeCompiler;

    /// <summary>
    /// Compile every message handler chain the host knows about. Once per host: a later call
    /// returns the first call's report (or rethrows its failure) without compiling again.
    /// </summary>
    public static HandlerWarmUpReport WarmUpHandlers(this IHost host)
        => ensureWarm(host.Services.GetRequiredService<IWolverineRuntime>(), allHandlers: true);

    /// <summary>
    /// What warming has done to this host so far — null when nothing has warmed it, or when the
    /// last attempt failed (that failure is what the next act rethrows).
    /// </summary>
    public static HandlerWarmUpReport? ReportFor(IHost host)
    {
        var runtime = host.Services.GetService<IWolverineRuntime>();
        if (runtime == null) return null;

        lock (gate)
        {
            return outcomes.TryGetValue(runtime, out var outcome) ? outcome.Report : null;
        }
    }

    /// <summary>
    /// The automatic path, run before every tracked session a step context opens. Does nothing
    /// when <see cref="Automatic"/> is off, when the host runs no Wolverine, or once the host is
    /// warm.
    /// </summary>
    internal static void WarmBeforeTracking(IHost host)
    {
        if (Automatic == AutomaticWarmUp.Off) return;

        var runtime = host.Services.GetService<IWolverineRuntime>();
        if (runtime != null) ensureWarm(runtime, Automatic == AutomaticWarmUp.AllHandlers);
    }

    private static HandlerWarmUpReport ensureWarm(IWolverineRuntime runtime, bool allHandlers)
    {
        Outcome outcome;
        lock (gate)
        {
            // A primed host asked for everything still owes the rest; a fully warmed (or failed)
            // host owes nothing.
            if (!outcomes.TryGetValue(runtime, out outcome!)
                || (allHandlers && outcome.Report is { AllHandlers: false }))
            {
                outcome = new Outcome();
                try
                {
                    outcome.Report = warm(runtime, allHandlers);
                }
                catch (HandlerWarmUpException e)
                {
                    outcome.Failure = e;
                }

                outcomes.AddOrUpdate(runtime, outcome);
            }
        }

        if (outcome.Failure != null) throw outcome.Failure;
        return outcome.Report!;
    }

    private static HandlerWarmUpReport warm(IWolverineRuntime runtime, bool allHandlers)
    {
        var watch = Stopwatch.StartNew();
        var warmed = 0;
        var skipped = new List<Type>();
        var failures = new List<(Type, Exception)>();

        // The handler graph is public on the concrete runtime, not on IWolverineRuntime. Anything
        // else (a substitute in a unit test) has no chains to warm.
        if (runtime is not WolverineRuntime concrete)
            return new HandlerWarmUpReport(0, skipped, allHandlers, watch.Elapsed);

        foreach (var chain in concrete.Handlers.Chains)
        {
            try
            {
                // Compiles the chain on first call, under Wolverine's own lock; a no-op afterwards.
                concrete.Handlers.HandlerFor(chain.MessageType);
                warmed++;
            }
            catch (NoHandlerForEndpointException)
            {
                // Only endpoint-specific handlers: there is no default handler to compile, and the
                // sticky ones compile per endpoint when a message arrives there.
                skipped.Add(chain.MessageType);
            }
            catch (Exception e)
            {
                failures.Add((chain.MessageType, e));
            }

            // One compiled chain is enough to have started the compiler.
            if (!allHandlers && warmed > 0) break;
        }

        if (failures.Count > 0) throw new HandlerWarmUpException(failures);

        return new HandlerWarmUpReport(warmed, skipped, allHandlers, watch.Elapsed);
    }
}

/// <summary>
/// A hosted service that compiles every Wolverine handler on a host, so no scenario's act — and no
/// scenario's timings — include codegen (bobcat#287). Optional: without it the first tracked act
/// primes the compiler itself.
/// </summary>
/// <remarks>
/// Register it AFTER the resource whose host it warms — one registration order now spans resources
/// and plain hosted services, so "after" is literal:
/// <code>
/// runner.Resources.Add(resource);
/// runner.Resources.Add(new WarmUpWolverineHandlers(resource));
/// </code>
/// </remarks>
public sealed class WarmUpWolverineHandlers : IHostedService
{
    private readonly IHostResource _resource;

    public WarmUpWolverineHandlers(IHostResource resource)
    {
        _resource = resource;
    }

    /// <summary>What the warm-up did, once it has run.</summary>
    public HandlerWarmUpReport? Report { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Report = _resource.Host.WarmUpHandlers();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
