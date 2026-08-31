using Xunit;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Wolverine.ErrorHandling;
using Wolverine.Redis.Internal;

namespace Wolverine.Redis.Tests.Persistence;

/// <summary>
/// The only tests here that prove anything about concurrency.
/// </summary>
/// <remarks>
/// The shared saga compliance specs are entirely sequential: one message, then the next. They pass just
/// as happily against a store that loses every concurrent write, which is exactly what a blind
/// last-write-wins <c>SET</c> would be. These drive two writers at one saga on purpose.
/// </remarks>
public class saga_optimistic_concurrency : IAsyncLifetime
{
    private ConnectionMultiplexer _multiplexer = null!;
    private string _prefix = null!;

    public ValueTask InitializeAsync()
    {
        _prefix = RedisPersistenceServer.UniquePrefix("concurrency");

        if (RedisPersistenceServer.IsRunning)
        {
            _multiplexer = RedisPersistenceServer.Connect();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _multiplexer?.Dispose();
        return ValueTask.CompletedTask;
    }

    private string keyFor(string id) => $"{_prefix}:{id}";

    private Task<IHost> buildHostAsync(Action<WolverineOptions>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton<IConnectionMultiplexer>(_multiplexer);
                opts.UseRedisPersistence(redis =>
                    redis.Saga<CounterSaga>(x => x.KeyFor = ctx => keyFor(ctx.Id.ToString()!)));

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<CounterSaga>();

                configure?.Invoke(opts);
            }).StartAsync();
    }

    /// <summary>
    /// The primitive itself, with no Wolverine in the way: a stale compare-and-swap must lose. Written
    /// first and deliberately at this level — everything else in this package is built on the claim
    /// that these three scripts behave, and a test that goes through the saga chain would not
    /// distinguish "the script refused" from "the chain never got there".
    /// </summary>
    [RedisFact]
    public async Task the_lua_compare_and_swap_refuses_a_stale_write()
    {
        var database = _multiplexer.GetDatabase();
        var key = keyFor("raw");

        async Task<long> evaluate(string script, params RedisValue[] values) =>
            (long)await database.ScriptEvaluateAsync(script, [key], values);

        (await evaluate(RedisSagaScripts.Insert, "first", 0)).ShouldBe(RedisSagaScripts.Applied);

        // A second creator loses rather than overwriting
        (await evaluate(RedisSagaScripts.Insert, "second", 0)).ShouldBe(RedisSagaScripts.VersionMismatch);
        ((string?)await database.HashGetAsync(key, RedisSagaScripts.DataField)).ShouldBe("first");

        // A writer holding revision 1 wins once...
        (await evaluate(RedisSagaScripts.Update, "1", "second", 0)).ShouldBe(RedisSagaScripts.Applied);
        ((string?)await database.HashGetAsync(key, RedisSagaScripts.VersionField)).ShouldBe("2");

        // ...and the writer still holding the now-stale revision 1 loses
        (await evaluate(RedisSagaScripts.Update, "1", "third", 0)).ShouldBe(RedisSagaScripts.VersionMismatch);
        ((string?)await database.HashGetAsync(key, RedisSagaScripts.DataField)).ShouldBe("second");

        // A stale delete loses too, so completing a saga cannot drop a write it never saw
        (await evaluate(RedisSagaScripts.Delete, "1")).ShouldBe(RedisSagaScripts.VersionMismatch);
        (await database.KeyExistsAsync(key)).ShouldBeTrue();

        (await evaluate(RedisSagaScripts.Delete, "2")).ShouldBe(RedisSagaScripts.Applied);
        (await database.KeyExistsAsync(key)).ShouldBeFalse();

        // And once it is gone, both writes report that rather than silently recreating it
        (await evaluate(RedisSagaScripts.Update, "2", "resurrected", 0)).ShouldBe(RedisSagaScripts.Missing);
        (await evaluate(RedisSagaScripts.Delete, "2")).ShouldBe(RedisSagaScripts.Missing);
    }

    [RedisFact]
    public async Task stale_write_is_surfaced_as_SagaConcurrencyException()
    {
        using var host = await buildHostAsync();
        var bus = host.MessageBus();

        var id = Guid.NewGuid().ToString();
        await bus.InvokeAsync(new StartCounter(id), TestContext.Current.CancellationToken);

        // Interfere exactly the way a second node would: the handler reads the saga, then another
        // writer commits a new revision of the same key before this message gets to write.
        await Should.ThrowAsync<SagaConcurrencyException>(() =>
            bus.InvokeAsync(new IncrementCounter(id) { InterfereEveryAttempt = true }));

        // The interfering writer's value survived — nothing was silently overwritten
        (await loadAsync(id))!.Count.ShouldBe(InterferingCount);
    }

    [RedisFact]
    public async Task concurrent_messages_against_one_saga_both_get_applied()
    {
        using var host = await buildHostAsync(opts =>
            opts.Policies.OnException<SagaConcurrencyException>().RetryTimes(5));

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new StartCounter(id), TestContext.Current.CancellationToken);

        // Both handlers pause between the saga read and the saga write, so both genuinely read Count=0.
        // Against a blind write the loser would overwrite the winner and Count would end at 1 with no
        // error at all. A bus per invocation because IMessageBus carries per-message context and is not
        // meant to be driven concurrently.
        var pause = TimeSpan.FromMilliseconds(500);
        await Task.WhenAll(
            host.MessageBus().InvokeAsync(new IncrementCounter(id) { Delay = pause },
                TestContext.Current.CancellationToken),
            host.MessageBus().InvokeAsync(new IncrementCounter(id) { Delay = pause },
                TestContext.Current.CancellationToken));

        (await loadAsync(id))!.Count.ShouldBe(2);
    }

    [RedisFact]
    public async Task stale_delete_of_a_completed_saga_is_surfaced_as_SagaConcurrencyException()
    {
        using var host = await buildHostAsync();
        var bus = host.MessageBus();

        var id = Guid.NewGuid().ToString();
        await bus.InvokeAsync(new StartCounter(id), TestContext.Current.CancellationToken);

        // Completing a saga deletes the key. A blind delete would drop the interfering writer's
        // revision just as silently as a blind write would.
        await Should.ThrowAsync<SagaConcurrencyException>(() => bus.InvokeAsync(new CompleteCounter(id)));

        (await loadAsync(id)).ShouldNotBeNull();
    }

    /// <summary>
    /// Two nodes handling a "start" message for the same identity at the same time. Only one saga may
    /// exist afterwards, and the loser has to be told — an insert that silently overwrote would throw
    /// away whatever the winner's handler had already put into the saga.
    /// </summary>
    [RedisFact]
    public async Task a_second_start_for_the_same_identity_loses_the_create_race()
    {
        using var host = await buildHostAsync();

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new StartCounter(id) { InitialCount = 7 },
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<SagaConcurrencyException>(() =>
            host.MessageBus().InvokeAsync(new StartCounter(id) { InitialCount = 99 }));

        (await loadAsync(id))!.Count.ShouldBe(7);
    }

    /// <summary>
    /// The saga was completed by another message while this one was mid-flight. Writing the state back
    /// would recreate a saga that is supposed to be over, so it has to be reported instead.
    /// </summary>
    [RedisFact]
    public async Task updating_a_saga_another_message_completed_does_not_resurrect_it()
    {
        using var host = await buildHostAsync();
        var bus = host.MessageBus();

        var id = Guid.NewGuid().ToString();
        await bus.InvokeAsync(new StartCounter(id), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<SagaConcurrencyException>(() =>
            bus.InvokeAsync(new IncrementCounter(id) { DeleteBeforeWriting = true }));

        (await loadAsync(id)).ShouldBeNull();
    }

    internal const int InterferingCount = 100;

    private async Task<CounterSaga?> loadAsync(string id)
    {
        var value = await _multiplexer.GetDatabase().HashGetAsync(keyFor(id), RedisSagaScripts.DataField);

        return value.IsNull
            ? null
            : JsonSerializer.Deserialize<CounterSaga>((byte[])value!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}

public record StartCounter(string Id)
{
    public int InitialCount { get; init; }
}

public record IncrementCounter(string Id)
{
    /// <summary>
    /// Commit a competing revision of the saga on every attempt, so the pending write can never win.
    /// Used to assert the violation is reported rather than swallowed.
    /// </summary>
    public bool InterfereEveryAttempt { get; init; }

    /// <summary>
    /// Complete the saga out from under this message, as another node finishing it would.
    /// </summary>
    public bool DeleteBeforeWriting { get; init; }

    /// <summary>
    /// Held between the saga read and the saga write, which is where a competing message's write lands
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
        return new CounterSaga { Id = command.Id, Count = command.InitialCount };
    }

    public async Task Handle(IncrementCounter command, IRedisDocumentSession session)
    {
        if (command.InterfereEveryAttempt)
        {
            await writeCompetingRevisionAsync(session, command.Id);
        }

        if (command.DeleteBeforeWriting)
        {
            await session.DeleteAsync(this, null);
        }

        if (command.Delay > TimeSpan.Zero)
        {
            await Task.Delay(command.Delay);
        }

        Count++;
    }

    public async Task Handle(CompleteCounter command, IRedisDocumentSession session)
    {
        await writeCompetingRevisionAsync(session, command.Id);
        MarkCompleted();
    }

    // Bumps the stored revision out from under the saga that is mid-flight, exactly as a second node
    // handling another message for this saga id would
    private static Task writeCompetingRevisionAsync(IRedisDocumentSession session, string id)
    {
        return session.StoreAsync(new CounterSaga
        {
            Id = id,
            Count = saga_optimistic_concurrency.InterferingCount
        }, null);
    }
}
