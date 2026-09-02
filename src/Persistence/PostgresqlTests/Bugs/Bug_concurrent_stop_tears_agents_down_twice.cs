using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests.Bugs;

/// <summary>
/// WolverineRuntime.StopAsync guards itself with a "have I already stopped" latch, but it used to set
/// that latch only AFTER awaiting the agent cancellation. Both IHostedService.StopAsync and
/// IAsyncDisposable.DisposeAsync route into it, so two callers could each read the latch as unset, pass
/// the guard, and then run the entire shutdown -- including teardownAgentsAsync -- concurrently. That
/// method clears every field it disposes, so the second pass saw its own
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
    public async Task second_caller_is_latched_out_before_the_first_yields()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "concurrent_stop");
                opts.Durability.Mode = DurabilityMode.Balanced;
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();

        // Both calls start on this thread, so the second one can only get past the guard if the first
        // one yielded before latching it.
        var first = runtime.StopAsync(CancellationToken.None);
        var second = runtime.StopAsync(CancellationToken.None);

        second.IsCompletedSuccessfully.ShouldBeTrue(
            "the second caller has to be latched out, or it runs the agent teardown concurrently with the first");

        await Task.WhenAll(first, second);
    }
}
