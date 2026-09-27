using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-4646, reported against 6.40.0 on a two-node Balanced cluster with a global partitioned topology
/// over sharded PostgreSQL queues. <c>EndpointCollection._listeners</c> was a plain
/// <see cref="Dictionary{TKey,TValue}"/>, written by both <c>StartListenerAsync</c> overloads with no
/// lock at all. <c>StartAgents.StartBatchAsync</c> fans agent starts out over
/// <c>Durability.MaxAgentStartParallelism</c> threads (10 by default), so a failover that handed one
/// node several <c>ExclusiveListenerAgent</c>s in a single batch had that many threads writing the
/// dictionary at once — while <c>GlobalPartitionedRoute</c> read it on every send.
///
/// <para>A <see cref="Dictionary{TKey,TValue}"/> caught mid-resize does not merely return a wrong
/// answer, it poisons the instance: every later lookup threw</para>
///
/// <code>
/// System.InvalidOperationException: Operations that change non-concurrent collections must have
/// exclusive access.
/// </code>
///
/// <para>In the report every publish through the partitioned topology failed from then on — 38 times
/// in four minutes out of a Marten subscription — and the node never recovered until the pod was
/// replaced. The reporter's workaround, <c>MaxAgentStartParallelism = 1</c>, narrows the window to
/// writer-versus-reader but does not close it.</para>
///
/// <para>⚠️ These are stress tests, so they cannot prove a race absent. The lost-registration assertion
/// is the load-bearing one: it fails on a corrupted or racily-grown dictionary without needing the
/// exception to be thrown on the exact round being observed.</para>
/// </summary>
public class listener_registration_is_not_raced_by_parallel_agent_starts_4646 : IAsyncLifetime
{
    private const int ListenerCount = 64;
    private const int Rounds = 10;

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Durability.Mode = DurabilityMode.Solo)
            .StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_parallel_batch_of_agent_starts_registers_every_listener()
    {
        var runtime = _host.GetRuntime();
        var token = TestContext.Current.CancellationToken;
        var missing = new List<string>();
        var started = 0;

        // Rounds, because one batch is not reliably enough to lose an entry: a single round of 64 came
        // up clean on roughly a third of runs against the unfixed code.
        for (var round = 0; round < Rounds; round++)
        {
            var endpoints = racedEndpoints($"batch{round}");
            await startInParallelAsync(runtime, endpoints, token);

            // The real damage. A Dictionary written from 10 threads drops entries as it resizes, so the
            // node is left believing it never took ownership of listeners it is in fact running -- which
            // is what makes GlobalPartitionedRoute ship to the broker for a slot this node already owns.
            started += endpoints.Length;
            missing.AddRange(endpoints
                .Where(x => runtime.Endpoints.FindListeningAgent(x.Uri) == null)
                .Select(x => x.Uri.ToString()));
        }

        missing.Take(5).ShouldBeEmpty($"{missing.Count} of {started} listener registrations were lost");
    }

    [Fact]
    public async Task sending_can_read_the_listeners_while_a_batch_starts()
    {
        var runtime = _host.GetRuntime();
        var token = TestContext.Current.CancellationToken;
        var endpoints = racedEndpoints("reading");

        // The corrupting write and the read that trips over it are in different methods, so this half
        // catches what the registration count cannot: a lookup landing inside a resize.
        await Should.NotThrowAsync(async () =>
        {
            // GlobalPartitionedRoute.CreateForSending asks FindListeningAgent on EVERY send to decide
            // whether this node already owns the slot, so the read side is as hot as the write side.
            using var starting = new CancellationTokenSource();
            var reading = Task.Run(async () =>
            {
                while (!starting.IsCancellationRequested)
                {
                    foreach (var endpoint in endpoints)
                    {
                        runtime.Endpoints.FindListeningAgent(endpoint.Uri);
                    }

                    await Task.Yield();
                }
            }, token);

            await startInParallelAsync(runtime, endpoints, token);
            await starting.CancelAsync();
            await reading;
        });
    }

    private static RacedListenerEndpoint[] racedEndpoints(string prefix)
    {
        return Enumerable.Range(0, ListenerCount)
            .Select(i => new RacedListenerEndpoint($"racedlistener://{prefix}-{i}".ToUri()))
            .ToArray();
    }

    private static Task startInParallelAsync(IWolverineRuntime runtime, RacedListenerEndpoint[] endpoints,
        CancellationToken token)
    {
        // Mirrors StartAgents.StartBatchAsync: the same bounded fan-out, at the same default degree.
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = runtime.Options.Durability.MaxAgentStartParallelism,
            CancellationToken = token
        };

        return Parallel.ForEachAsync(endpoints, options,
            async (endpoint, _) => await runtime.Endpoints.StartListenerAsync(endpoint, token));
    }
}

/// <summary>
/// Stands in for one shard of a <c>UseShardedPostgresqlQueues</c> topology: an endpoint that starts a
/// listener cheaply, so a batch of them can be started in parallel without a broker. Deliberately not
/// registered with any transport -- <c>StartListenerAsync</c> compiles and starts whatever endpoint it
/// is handed, which is what an <c>ExclusiveListenerAgent</c> does.
/// </summary>
public class RacedListenerEndpoint : Endpoint
{
    public RacedListenerEndpoint(Uri uri) : base(uri, EndpointRole.Application)
    {
        IsListener = true;
        EndpointName = uri.Host;
        Mode = EndpointMode.Inline;
        ListenerScope = ListenerScope.Exclusive;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return ValueTask.FromResult<IListener>(new RacedListener(Uri));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException();
    }
}

internal class RacedListener(Uri address) : IListener
{
    public Uri Address { get; } = address;
    public IHandlerPipeline? Pipeline => null;
    public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public ValueTask StopAsync() => ValueTask.CompletedTask;
}
