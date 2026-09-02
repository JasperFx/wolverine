using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace PostgresqlTests.Bugs;

/// <summary>
/// WolverineRuntime.StopAsync guards itself against running twice, but it used to read that guard,
/// await the agent cancellation, and only then set it. Both IHostedService.StopAsync and
/// IAsyncDisposable.DisposeAsync route into it, so two callers could each see "not stopped", pass the
/// guard, and run the entire shutdown concurrently. teardownAgentsAsync is not written for that: it
/// clears every field it disposes, so the second pass saw its own
/// <c>NodeController?.DeferredWork != null</c> check pass and then dereferenced a field the first pass
/// had already cleared, throwing NullReferenceException out of IHost.StopAsync:
///
/// <code>
/// System.NullReferenceException : Object reference not set to an instance of an object.
///    at Wolverine.Runtime.WolverineRuntime.teardownAgentsAsync() in WolverineRuntime.Agents.cs:line 436
///    at Wolverine.Runtime.WolverineRuntime.StopAsync(CancellationToken cancellationToken)
///    at Microsoft.Extensions.Hosting.Internal.Host.StopAsync(CancellationToken cancellationToken)
/// </code>
///
/// A Balanced node is what makes this reachable: only Balanced builds the DeferredWork runner, and only
/// Balanced puts a linked token source on the agent cancellation, which is what makes CancelAsync()
/// genuinely yield and open the window. Hence a real message store here rather than CoreTests.
/// </summary>
public class Bug_concurrent_stop_tears_agents_down_twice : PostgresqlContext
{
    [Fact]
    public async Task two_concurrent_callers_only_tear_the_node_down_once()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "concurrent_stop");
                opts.Durability.Mode = DurabilityMode.Balanced;
            }).Build();

        // NodeAgentController.StopAsync is the tail of the agent teardown and is not itself guarded: it
        // deletes this node's row and reports NodeStopped() every time it runs, so the call count is a
        // direct count of teardown passes. The controller captures runtime.Observer in its constructor,
        // which happens during StartAsync, so this has to be swapped in before the host starts.
        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        var observer = Substitute.For<IWolverineObserver>();
        runtime.Observer = observer;

        await host.StartAsync(TestContext.Current.CancellationToken);

        // Both calls start on this thread, so the second one can only run a shutdown of its own if the
        // first yielded before claiming it.
        var first = runtime.StopAsync(CancellationToken.None);
        var second = runtime.StopAsync(CancellationToken.None);

        await Task.WhenAll(first, second);

        await observer.Received(1).NodeStopped();

        // And the claim is joinable rather than skippable, which is what keeps
        // IAsyncDisposable.DisposeAsync from disposing endpoints and transports out from under a
        // shutdown that is still running: every later caller gets the one shutdown, finished.
        runtime.StopAsync(CancellationToken.None).IsCompletedSuccessfully.ShouldBeTrue();
    }
}
