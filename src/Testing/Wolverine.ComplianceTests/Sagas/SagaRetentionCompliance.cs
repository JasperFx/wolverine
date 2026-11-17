using System.Collections.Concurrent;
using JasperFx.Core;
using JasperFx.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.Sagas;

// String ids, because RavenDB only stores a saga with a string identity
public record StartRetainedSaga(string Id);

public record StartAndFinishRetainedSaga(string Id);

public record TouchRetainedSaga(string Id);

public record FinishRetainedSaga(string Id);

public record LateRetainedSagaMessage(string Id);

public record OrphanRetainedSagaMessage(string Id);

public record RestartRetainedSaga(string Id);

public record RetainedSagaTimeout(string Id) : TimeoutMessage(1.Hours());

[RetainWhenCompleted(Days = 2)]
public class RetainedSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public int Touches { get; set; }

    public static RetainedSaga Start(StartRetainedSaga command) => new() { Id = command.Id };

    public static RetainedSaga Start(StartAndFinishRetainedSaga command)
    {
        var saga = new RetainedSaga { Id = command.Id };
        saga.MarkCompleted();
        return saga;
    }

    public void Handle(TouchRetainedSaga command) => Touches++;

    public void Handle(FinishRetainedSaga command) => MarkCompleted();

    public void Handle(LateRetainedSagaMessage command) => Touches++;

    // Runs for a message that arrives after the saga completed
    public static void NotFound(LateRetainedSagaMessage command) => NotFoundIds.Add(command.Id);

    public void Handle(OrphanRetainedSagaMessage command) => Touches++;

    public void Handle(RetainedSagaTimeout timeout) => Touches++;

    public void StartOrHandle(RestartRetainedSaga command) => Touches++;

    public static readonly ConcurrentBag<string> NotFoundIds = new();
}

/// <summary>
/// GH-5572. What every saga storage that supports <see cref="RetainWhenCompletedAttribute" /> must do, against a real
/// host. Counts are taken as differences, so instances left over from earlier runs never change the answer.
/// </summary>
public abstract class SagaRetentionCompliance : IAsyncLifetime
{
    private static readonly string SagaTypeName = typeof(RetainedSaga).FullName!;

    protected IHost Host { get; private set; } = null!;

    /// <summary>
    /// Configure the saga storage under test. The handler discovery is already restricted to
    /// <see cref="RetainedSaga" />, and the host runs in Solo mode.
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RetainedSaga>();
                opts.Durability.Mode = DurabilityMode.Solo;
                ConfigureStorage(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        await CleanUpAsync();
    }

    /// <summary>Release anything <see cref="ConfigureStorage" /> created, such as a SQLite database file.</summary>
    protected virtual ValueTask CleanUpAsync() => ValueTask.CompletedTask;

    private ISagaStoreDiagnostics sagas => Host.GetRuntime().SagaStorage;

    private ISagaRetentionSweeper sweeper => Host.Services.GetServices<ISagaStoreDiagnostics>()
        .OfType<ISagaRetentionSweeper>()
        .Single(x => x.SagaTypesForRetention().Contains(typeof(RetainedSaga)));

    private static string newId() => Guid.NewGuid().ToString();

    private async Task<RetainedSagaState?> readAsync(string id)
    {
        var state = await sagas.ReadSagaAsync(SagaTypeName, id, TestContext.Current.CancellationToken);
        if (state == null) return null;

        var touches = 0;
        foreach (var property in state.State.EnumerateObject())
        {
            if (property.Name.Equals(nameof(RetainedSaga.Touches), StringComparison.OrdinalIgnoreCase))
            {
                touches = property.Value.GetInt32();
            }
        }

        return new RetainedSagaState(state.IsCompleted, touches);
    }

    private async Task<string> startAndFinishAsync()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartRetainedSaga(id));
        await Host.InvokeMessageAndWaitAsync(new TouchRetainedSaga(id));
        await Host.InvokeMessageAndWaitAsync(new FinishRetainedSaga(id));
        return id;
    }

    [Fact]
    public async Task a_completed_saga_is_kept_and_marked_completed()
    {
        var id = await startAndFinishAsync();

        var state = await readAsync(id);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeTrue();
        state.Touches.ShouldBe(1);
    }

    [Fact]
    public async Task a_saga_that_completes_as_it_starts_is_kept()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartAndFinishRetainedSaga(id));

        var state = await readAsync(id);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task an_open_saga_is_not_marked_completed()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartRetainedSaga(id));
        await Host.InvokeMessageAndWaitAsync(new TouchRetainedSaga(id));

        var state = await readAsync(id);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.Touches.ShouldBe(1);
    }

    [Fact]
    public async Task a_later_message_for_a_completed_saga_goes_to_not_found()
    {
        var id = await startAndFinishAsync();

        await Host.InvokeMessageAndWaitAsync(new LateRetainedSagaMessage(id));

        RetainedSaga.NotFoundIds.ShouldContain(id);
        (await readAsync(id))!.Touches.ShouldBe(1);
    }

    [Fact]
    public async Task a_later_message_without_not_found_is_an_unknown_saga()
    {
        var id = await startAndFinishAsync();

        await Should.ThrowAsync<UnknownSagaException>(() =>
            Host.MessageBus().InvokeAsync(new OrphanRetainedSagaMessage(id), TestContext.Current.CancellationToken));

        (await readAsync(id))!.Touches.ShouldBe(1);
    }

    [Fact]
    public async Task a_timeout_for_a_completed_saga_is_ignored()
    {
        var id = await startAndFinishAsync();

        await Host.InvokeMessageAndWaitAsync(new RetainedSagaTimeout(id));

        (await readAsync(id))!.Touches.ShouldBe(1);
    }

    [Fact]
    public async Task a_start_or_handle_message_does_not_restart_a_completed_saga()
    {
        var id = await startAndFinishAsync();

        await Host.InvokeMessageAndWaitAsync(new RestartRetainedSaga(id));

        var state = await readAsync(id);
        state!.IsCompleted.ShouldBeTrue();
        state.Touches.ShouldBe(1);
    }

    [Fact]
    public async Task queries_leave_completed_sagas_out_unless_asked()
    {
        var token = TestContext.Current.CancellationToken;
        var options = new DocumentQueryOptions(1, 10);

        var openBefore = (await sagas.QuerySagasAsync(SagaTypeName, options, token)).TotalCount;
        var allBefore = (await sagas.QuerySagasAsync(SagaTypeName, options, true, token)).TotalCount;
        var idleBefore = await countIdleAsync();

        var open = newId();
        await Host.InvokeMessageAndWaitAsync(new StartRetainedSaga(open));
        await startAndFinishAsync();

        (await sagas.QuerySagasAsync(SagaTypeName, options, token)).TotalCount.ShouldBe(openBefore + 1);
        (await sagas.QuerySagasAsync(SagaTypeName, options, false, token)).TotalCount.ShouldBe(openBefore + 1);
        (await sagas.QuerySagasAsync(SagaTypeName, options, true, token)).TotalCount.ShouldBe(allBefore + 2);

        // A completed saga is not idle
        if (idleBefore.HasValue)
        {
            (await countIdleAsync()).ShouldBe(idleBefore + 1);
        }
    }

    private async Task<long?> countIdleAsync()
    {
        if (sagas is not ISagaIdleDiagnostics idle || !idle.SupportsIdleQueries(SagaTypeName)) return null;
        return await idle.CountIdleSagasAsync(SagaTypeName, DateTimeOffset.UtcNow.AddMinutes(5), null,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_sweep_deletes_only_completed_sagas_past_the_cutoff()
    {
        var token = TestContext.Current.CancellationToken;

        var open = newId();
        await Host.InvokeMessageAndWaitAsync(new StartRetainedSaga(open));
        var completed = await startAndFinishAsync();

        // Completed after this cutoff, so kept
        await sweeper.DeleteCompletedSagasAsync(typeof(RetainedSaga), DateTimeOffset.UtcNow.AddHours(-1), token);
        (await readAsync(completed)).ShouldNotBeNull();

        var deleted =
            await sweeper.DeleteCompletedSagasAsync(typeof(RetainedSaga), DateTimeOffset.UtcNow.AddMinutes(1), token);
        deleted.ShouldBeGreaterThanOrEqualTo(1);

        (await readAsync(completed)).ShouldBeNull();
        (await readAsync(open)).ShouldNotBeNull();
    }

    [Fact]
    public async Task the_retention_agent_runs_for_the_store()
    {
        var expected = $"{SagaRetentionAgentFamilyScheme}:///{Uri.EscapeDataString(sweeper.RetentionStoreName)}";

        var runtime = Host.GetRuntime();
        for (var i = 0; i < 100; i++)
        {
            if (runtime.Agents.AllRunningAgentUris().Any(x => x.ToString() == expected)) return;
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        runtime.Agents.AllRunningAgentUris().Select(x => x.ToString()).ShouldContain(expected);
    }

    private const string SagaRetentionAgentFamilyScheme = "wolverine-saga-retention";
}

public record RetainedSagaState(bool IsCompleted, int Touches);
