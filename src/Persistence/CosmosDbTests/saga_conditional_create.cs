using System.Collections.Concurrent;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace CosmosDbTests;

/// <summary>
///     Starting a saga used to write it with an unconditional upsert, so a second start message for a saga id that
///     already had a running saga silently replaced it. Starting a saga is now a conditional create: CosmosDB's 409
///     Conflict surfaces as the same <see cref="SagaConcurrencyException" /> Marten, Azure Blob and S3 raise for a
///     duplicate start.
/// </summary>
[Collection("cosmosdb")]
public class saga_conditional_create
{
    private readonly AppFixture _fixture;

    public saga_conditional_create(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task duplicate_start_is_surfaced_as_SagaConcurrencyException(bool partitionById)
    {
        using var host = await buildHostAsync(partitionById);

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new StartGuarded(id, 1), TestContext.Current.CancellationToken);

        var original = await readAsync(id, partitionById);
        original.ShouldNotBeNull();

        await Should.ThrowAsync<SagaConcurrencyException>(() =>
            host.MessageBus().InvokeAsync(new StartGuarded(id, 2), TestContext.Current.CancellationToken));

        // The running saga was not touched: same state, same revision
        var after = await readAsync(id, partitionById);
        after!.Resource.Value.ShouldBe(1);
        after.ETag.ShouldBe(original.ETag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task duplicate_start_does_not_send_its_cascaded_messages(bool partitionById)
    {
        using var host = await buildHostAsync(partitionById);

        var id = Guid.NewGuid().ToString();
        await host.InvokeMessageAndWaitAsync(new StartGuarded(id, 1));
        GuardedStartedHandler.ValuesFor(id).ShouldBe([1]);

        var tracked = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new StartGuarded(id, 2));

        tracked.AllExceptions().OfType<SagaConcurrencyException>().ShouldNotBeEmpty();

        // The create failed before the outgoing messages were flushed, so the duplicate's notice went nowhere
        tracked.Sent.MessagesOf<GuardedStarted>().ShouldBeEmpty();
        GuardedStartedHandler.ValuesFor(id).ShouldBe([1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task racing_starts_create_one_saga_and_the_loser_gets_SagaConcurrencyException(bool partitionById)
    {
        using var host = await buildHostAsync(partitionById);

        // Both messages find no saga, pause, then both try to create one
        var id = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(attemptAsync(host, new EnsureRaced(id)), attemptAsync(host, new EnsureRaced(id)));

        results.Count(x => x == null).ShouldBe(1);
        results.Single(x => x != null).ShouldBeOfType<SagaConcurrencyException>();

        var saga = await readAsync<RacedSaga>(id, partitionById);
        saga!.Resource.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task racing_starts_both_get_applied_with_a_retry(bool partitionById)
    {
        using var host = await buildHostAsync(partitionById,
            opts => opts.Policies.OnException<SagaConcurrencyException>().RetryTimes(3));

        // The loser's retry finds the winner's saga and takes the existing-saga path. Before the conditional
        // create, both blindly wrote Count = 1 and one message was lost without any error
        var id = Guid.NewGuid().ToString();
        await Task.WhenAll(
            host.MessageBus().InvokeAsync(new EnsureRaced(id), TestContext.Current.CancellationToken),
            host.MessageBus().InvokeAsync(new EnsureRaced(id), TestContext.Current.CancellationToken));

        var saga = await readAsync<RacedSaga>(id, partitionById);
        saga!.Resource.Count.ShouldBe(2);
    }

    /// <summary>
    ///     GH-2073: a saga that completes in its own start is never written, so there is nothing to conflict with
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task start_that_completes_immediately_is_not_inserted(bool partitionById)
    {
        using var host = await buildHostAsync(partitionById);

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new StartGuardedCompleted(id), TestContext.Current.CancellationToken);
        await host.MessageBus().InvokeAsync(new StartGuardedCompleted(id), TestContext.Current.CancellationToken);

        (await readAsync(id, partitionById)).ShouldBeNull();
    }

    /// <summary>
    ///     An <see cref="Insert{T}" /> return value is a "write this entity" side effect rather than a saga start,
    ///     so it keeps its upsert semantics
    /// </summary>
    [Fact]
    public async Task insert_side_effect_still_upserts()
    {
        using var host = await buildHostAsync(partitionById: false);

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new InsertGuardedDocument(id, 1), TestContext.Current.CancellationToken);
        await host.MessageBus().InvokeAsync(new InsertGuardedDocument(id, 2), TestContext.Current.CancellationToken);

        var response = await _fixture.Container.ReadItemAsync<GuardedDocument>(id, PartitionKey.None,
            cancellationToken: TestContext.Current.CancellationToken);
        response.Resource.Value.ShouldBe(2);
    }

    private static async Task<Exception?> attemptAsync(IHost host, object message)
    {
        try
        {
            // A bus per invocation because IMessageBus carries per-message context
            await host.MessageBus().InvokeAsync(message);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private Task<IHost> buildHostAsync(bool partitionById, Action<WolverineOptions>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton(_fixture.Client);
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName, cosmos =>
                {
                    if (partitionById)
                    {
                        cosmos.PartitionSagasById();
                    }
                });

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<GuardedSaga>();
                opts.Discovery.IncludeType<RacedSaga>();
                opts.Discovery.IncludeType(typeof(GuardedStartedHandler));
                opts.Discovery.IncludeType(typeof(InsertGuardedDocumentHandler));

                configure?.Invoke(opts);
            }).StartAsync();
    }

    private Task<ItemResponse<GuardedSaga>?> readAsync(string id, bool partitionById)
    {
        return readAsync<GuardedSaga>(id, partitionById);
    }

    private async Task<ItemResponse<T>?> readAsync<T>(string id, bool partitionById)
    {
        try
        {
            return await _fixture.Container.ReadItemAsync<T>(id,
                partitionById ? new PartitionKey(id) : PartitionKey.None);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}

public record StartGuarded(string Id, int Value);

public record StartGuardedCompleted(string Id);

public record GuardedStarted(string Id, int Value);

public class GuardedSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public int Value { get; set; }

    public static (GuardedSaga, GuardedStarted) Start(StartGuarded command)
    {
        return (new GuardedSaga { Id = command.Id, Value = command.Value },
            new GuardedStarted(command.Id, command.Value));
    }

    public static GuardedSaga Start(StartGuardedCompleted command)
    {
        var saga = new GuardedSaga { Id = command.Id };
        saga.MarkCompleted();
        return saga;
    }
}

public static class GuardedStartedHandler
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _received = new();

    public static int[] ValuesFor(string id)
    {
        return _received.TryGetValue(id, out var values) ? values.ToArray() : [];
    }

    public static void Handle(GuardedStarted message)
    {
        _received.GetOrAdd(message.Id, _ => new ConcurrentQueue<int>()).Enqueue(message.Value);
    }
}

public record EnsureRaced(string Id);

public class RacedSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public int Count { get; set; }

    public async Task StartOrHandle(EnsureRaced command)
    {
        // Only a saga that is being started is still at zero. Neither starting attempt may write until both
        // have got here, so both are guaranteed to have found no saga and both go on to create one
        if (Count == 0)
        {
            Id = command.Id;
            await StartRendezvous.ArriveAsync(command.Id, participants: 2, TimeSpan.FromSeconds(30));
        }

        Count++;
    }
}

/// <summary>
///     Holds each arrival for a given id until the expected number of participants have all arrived. Times out
///     rather than hanging the suite if one of them never does.
/// </summary>
public static class StartRendezvous
{
    private static readonly ConcurrentDictionary<string, Arrivals> _arrivals = new();

    public static Task ArriveAsync(string id, int participants, TimeSpan timeout)
    {
        var arrivals = _arrivals.GetOrAdd(id, _ => new Arrivals(participants));
        arrivals.Arrive();
        return arrivals.AllArrived.WaitAsync(timeout);
    }

    private class Arrivals(int participants)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task AllArrived => _allArrived.Task;

        public void Arrive()
        {
            if (Interlocked.Increment(ref _count) >= participants)
            {
                _allArrived.TrySetResult();
            }
        }
    }
}

public record InsertGuardedDocument(string Id, int Value);

public class GuardedDocument
{
    public string Id { get; set; } = string.Empty;
    public int Value { get; set; }
}

public static class InsertGuardedDocumentHandler
{
    public static Insert<GuardedDocument> Handle(InsertGuardedDocument command)
    {
        return Storage.Insert(new GuardedDocument { Id = command.Id, Value = command.Value });
    }
}
