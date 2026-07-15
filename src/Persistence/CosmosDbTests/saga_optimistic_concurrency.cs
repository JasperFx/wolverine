using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.ErrorHandling;

namespace CosmosDbTests;

/// <summary>
///     GH-3414. The CosmosDB saga frames used to persist with a blind <c>UpsertItemAsync</c>: no ETag was
///     captured on the point read and no <c>IfMatchEtag</c> was set on the write, so two messages for the
///     same saga id could both read the same revision and the second write would silently overwrite the
///     first. The saga now reads its ETag and writes compare-and-swap, surfacing CosmosDB's 412 as a
///     <see cref="SagaConcurrencyException" /> that Wolverine's existing retry machinery can act on.
/// </summary>
[Collection("cosmosdb")]
public class saga_optimistic_concurrency
{
    private readonly AppFixture _fixture;

    public saga_optimistic_concurrency(AppFixture fixture)
    {
        _fixture = fixture;
    }

    private Task<IHost> buildHostAsync(Action<WolverineOptions>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton(_fixture.Client);
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<CounterSaga>();

                configure?.Invoke(opts);
            }).StartAsync();
    }

    [Fact]
    public async Task stale_write_is_surfaced_as_SagaConcurrencyException()
    {
        await _fixture.ClearAll();

        using var host = await buildHostAsync();
        var bus = host.MessageBus();

        var id = Guid.NewGuid().ToString();
        await bus.InvokeAsync(new StartCounter(id));

        // Interfere exactly the way a second node would: the handler reads the saga, then another writer
        // commits a new revision of the same document before this message gets to write. Without the
        // IfMatchEtag the upsert below would happily clobber that revision.
        await Should.ThrowAsync<SagaConcurrencyException>(() =>
            bus.InvokeAsync(new IncrementCounter(id) { InterfereEveryAttempt = true }));

        // The interfering writer's value survived — nothing was silently overwritten
        var saga = await loadAsync(id);
        saga!.Count.ShouldBe(InterferingCount);
    }

    [Fact]
    public async Task concurrent_messages_against_one_saga_both_get_applied()
    {
        await _fixture.ClearAll();

        using var host = await buildHostAsync(opts =>
            opts.Policies.OnException<SagaConcurrencyException>().RetryTimes(5));

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new StartCounter(id));

        // Both handlers pause between the saga read and the saga write, so both genuinely read Count = 0.
        // Pre-fix, the loser's blind upsert overwrote the winner and Count ended at 1 with no error at all.
        // A bus per invocation because IMessageBus carries per-message context and is not meant to be
        // driven concurrently.
        var pause = TimeSpan.FromMilliseconds(500);
        await Task.WhenAll(
            host.MessageBus().InvokeAsync(new IncrementCounter(id) { Delay = pause }),
            host.MessageBus().InvokeAsync(new IncrementCounter(id) { Delay = pause }));

        var saga = await loadAsync(id);
        saga!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task stale_delete_of_a_completed_saga_is_surfaced_as_SagaConcurrencyException()
    {
        await _fixture.ClearAll();

        using var host = await buildHostAsync();
        var bus = host.MessageBus();

        var id = Guid.NewGuid().ToString();
        await bus.InvokeAsync(new StartCounter(id));

        // Completing a saga deletes the document. A blind delete would drop the interfering writer's
        // revision just as silently as a blind upsert would.
        await Should.ThrowAsync<SagaConcurrencyException>(() =>
            bus.InvokeAsync(new CompleteCounter(id)));

        (await loadAsync(id)).ShouldNotBeNull();
    }

    internal const int InterferingCount = 100;

    private async Task<CounterSaga?> loadAsync(string id)
    {
        try
        {
            var response = await _fixture.Container.ReadItemAsync<CounterSaga>(id, PartitionKey.None);
            return response.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}

public record StartCounter(string Id);

public record IncrementCounter(string Id)
{
    /// <summary>
    ///     Commit a competing revision of the saga document on every attempt, so the pending write can
    ///     never win. Used to assert the violation is reported rather than swallowed.
    /// </summary>
    public bool InterfereEveryAttempt { get; init; }

    /// <summary>
    ///     Held between the saga read and the saga write, which is where a competing message's write lands
    /// </summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
}

public record CompleteCounter(string Id);

public class CounterSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public int Count { get; set; }

    public static CounterSaga Start(StartCounter command)
    {
        return new CounterSaga { Id = command.Id };
    }

    public async Task Handle(IncrementCounter command, Container container)
    {
        if (command.InterfereEveryAttempt)
        {
            await writeCompetingRevisionAsync(container, command.Id);
        }

        if (command.Delay > TimeSpan.Zero)
        {
            await Task.Delay(command.Delay);
        }

        Count++;
    }

    public async Task Handle(CompleteCounter command, Container container)
    {
        await writeCompetingRevisionAsync(container, command.Id);
        MarkCompleted();
    }

    // Bumps the stored document's ETag out from under the saga that is mid-flight, exactly as a second
    // node handling another message for this saga id would
    private static Task writeCompetingRevisionAsync(Container container, string id)
    {
        return container.UpsertItemAsync(new CounterSaga
        {
            Id = id,
            Count = saga_optimistic_concurrency.InterferingCount
        });
    }
}
