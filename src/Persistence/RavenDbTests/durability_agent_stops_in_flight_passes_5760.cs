using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.RavenDb;

namespace RavenDbTests;

/// <summary>
/// GH-5760, the RavenDB side of GH-5247. Stopping the durability agent used to cancel its recovery and scheduled-job
/// loops and return at once, leaving a pass already in flight running against the database. A test driver that deletes
/// the database straight after the host stops then waited on that work, and under load the delete timed out
/// ("Waited for 00:00:15 for task with index ... to complete"). Stopping now waits for the pass.
/// </summary>
[Collection("raven")]
public class durability_agent_stops_in_flight_passes_5760 : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private DocumentStore _store = null!;
    private IHost _host = null!;

    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;
    private bool _stopped;
    private int _requestsAfterStop;

    public durability_agent_stops_in_flight_passes_5760(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        // OnBeforeRequest can only be attached before a store is initialized, so open a store of our own on the
        // fixture's database
        var database = _fixture.StartRavenStore();
        _store = new DocumentStore { Urls = database.Urls, Database = database.Database };

        _store.OnBeforeRequest += (_, args) =>
        {
            if (Volatile.Read(ref _stopped))
            {
                Interlocked.Increment(ref _requestsAfterStop);
                return;
            }

            // Hold exactly one query from the agent's background passes, once armed
            if (args.Url.Contains("/queries") && Interlocked.CompareExchange(ref _armed, 2, 1) == 1)
            {
                _blocked.TrySetResult();
                _release.Wait();
            }
        };

        _store.Initialize();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IDocumentStore>(_store);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                opts.ServiceName = "durability-agent-stops-5760";
                opts.UseRavenDbPersistence();
            }).StartAsync();
    }

    public ValueTask DisposeAsync()
    {
        _release.Set();
        _host.Dispose();
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task stopping_waits_for_a_pass_in_flight_and_nothing_reaches_the_database_after()
    {
        // A recovery or metrics pass is now held inside its query, before the host is told to stop
        Interlocked.Exchange(ref _armed, 1);
        await _blocked.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        var stop = _host.StopAsync(TestContext.Current.CancellationToken);

        // Before GH-5760 the stop returned here, with the query still running against the database
        (await Task.WhenAny(stop, Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken)))
            .ShouldNotBe(stop);

        _release.Set();
        await stop;
        Volatile.Write(ref _stopped, true);

        // Two polling intervals and more: no pass is left running to touch the database
        await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);
        Volatile.Read(ref _requestsAfterStop).ShouldBe(0);
    }
}
