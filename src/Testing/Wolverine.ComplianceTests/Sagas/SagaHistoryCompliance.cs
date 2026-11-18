using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.ComplianceTests.Sagas;

// GH-5665. String ids, because RavenDB and CosmosDB only store a saga with a string identity
public record StartHistorySaga(string Id);

public record AdvanceHistorySaga(string Id);

public record FinishHistorySaga(string Id);

public record FailHistorySaga(string Id);

/// <summary>Cascaded by <see cref="HistorySaga" />, so the history shows what the saga sent</summary>
public record HistorySagaAdvanced(string Id);

public record HistorySagaTimeout(string Id) : TimeoutMessage(1.Hours());

[RecordSagaHistory]
public class HistorySaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public int Advances { get; set; }

    public static (HistorySaga, HistorySagaTimeout) Start(StartHistorySaga command)
        => (new HistorySaga { Id = command.Id }, new HistorySagaTimeout(command.Id));

    public HistorySagaAdvanced Handle(AdvanceHistorySaga command)
    {
        Advances++;
        return new HistorySagaAdvanced(Id);
    }

    public void Handle(FinishHistorySaga command) => MarkCompleted();

    public void Handle(FailHistorySaga command) => throw new InvalidOperationException("You shall not pass");

    public void Handle(HistorySagaTimeout timeout) => MarkCompleted();
}

public static class HistorySagaAdvancedHandler
{
    public static void Handle(HistorySagaAdvanced message)
    {
        // Nothing, it only has to be routed somewhere
    }
}

public record StartRetainedHistorySaga(string Id);

public record FinishRetainedHistorySaga(string Id);

public record LateRetainedHistoryMessage(string Id);

[RecordSagaHistory]
[RetainWhenCompleted(Days = 2)]
public class RetainedHistorySaga : Saga
{
    public string Id { get; set; } = string.Empty;

    public static RetainedHistorySaga Start(StartRetainedHistorySaga command) => new() { Id = command.Id };

    public void Handle(FinishRetainedHistorySaga command) => MarkCompleted();

    public void Handle(LateRetainedHistoryMessage command)
    {
    }

    // Runs for a message that arrives after the saga completed
    public static void NotFound(LateRetainedHistoryMessage command)
    {
    }
}

/// <summary>
/// GH-5665. What every saga storage must do with <see cref="RecordSagaHistoryAttribute" />, against a real host: a row
/// per handled message, written with the saga, read through <see cref="ISagaHistoryDiagnostics" />, and kept exactly as
/// long as the saga. Every saga id is new, so rows left over from earlier runs never change the answer.
/// </summary>
public abstract class SagaHistoryCompliance : IAsyncLifetime
{
    protected IHost Host { get; private set; } = null!;

    /// <summary>
    /// Configure the saga storage under test. Handler discovery is already restricted to the history sagas, and the
    /// host runs in Solo mode.
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<HistorySaga>()
                    .IncludeType<RetainedHistorySaga>()
                    .IncludeType(typeof(HistorySagaAdvancedHandler));
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

    private ISagaHistoryDiagnostics history => (ISagaHistoryDiagnostics)Host.GetRuntime().SagaStorage;

    private static string newId() => Guid.NewGuid().ToString();

    private Task<SagaHistoryPage> readAsync<TSaga>(string id, int page = 1, int pageSize = 50) where TSaga : Saga
        => history.ReadSagaHistoryAsync(typeof(TSaga).FullName!, id, new SagaHistoryPageRequest(page, pageSize),
            TestContext.Current.CancellationToken);

    private static string outcomeOf(SagaHistoryEvent entry) => entry.Data.GetProperty("Outcome").GetString()!;

    private static string[] sentTypesOf(SagaHistoryEvent entry) => entry.Data.GetProperty("SentMessages")
        .EnumerateArray().Select(x => x.GetProperty("MessageType").GetString()!).ToArray();

    [Fact]
    public async Task the_saga_is_described_as_keeping_a_message_log()
    {
        var descriptors = await Host.GetRuntime().SagaStorage
            .GetRegisteredSagasAsync(TestContext.Current.CancellationToken);

        var descriptor = descriptors.Single(x => x.StateType.FullName == typeof(HistorySaga).FullName);
        descriptor.SupportsHistory.ShouldBeTrue();
        descriptor.HistoryKind.ShouldBe(SagaHistoryKind.MessageLog);
    }

    [Fact]
    public async Task the_starting_message_is_recorded_with_the_timeout_it_scheduled()
    {
        var id = newId();
        var session = await Host.InvokeMessageAndWaitAsync(new StartHistorySaga(id));

        var page = await readAsync<HistorySaga>(id);
        page.TotalCount.ShouldBe(1);

        var entry = page.Events.Single();
        entry.Version.ShouldBe(1);
        entry.EventTypeName.ShouldBe(typeof(StartHistorySaga).ToMessageTypeName());
        entry.DotNetTypeName.ShouldBe(typeof(StartHistorySaga).FullName);
        outcomeOf(entry).ShouldBe(nameof(SagaHistoryOutcome.Started));
        entry.Data.GetProperty("Handler").GetString().ShouldBe($"{nameof(HistorySaga)}.Start");
        entry.Data.GetProperty("MessageId").GetGuid()
            .ShouldBe(session.Executed.Envelopes().Single(x => x.Message is StartHistorySaga).Id);

        var timeout = entry.Data.GetProperty("SentMessages").EnumerateArray().Single();
        timeout.GetProperty("MessageType").GetString().ShouldBe(typeof(HistorySagaTimeout).ToMessageTypeName());
        timeout.GetProperty("ScheduledTime").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task each_handled_message_is_recorded_with_the_messages_it_sent()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartHistorySaga(id));
        await Host.InvokeMessageAndWaitAsync(new AdvanceHistorySaga(id));

        var page = await readAsync<HistorySaga>(id);
        page.Events.Select(x => x.EventTypeName).ShouldBe([
            typeof(StartHistorySaga).ToMessageTypeName(),
            typeof(AdvanceHistorySaga).ToMessageTypeName()
        ]);

        var advanced = page.Events[1];
        advanced.Version.ShouldBe(2);
        outcomeOf(advanced).ShouldBe(nameof(SagaHistoryOutcome.Continued));
        advanced.Data.GetProperty("Handler").GetString().ShouldBe($"{nameof(HistorySaga)}.Handle");
        sentTypesOf(advanced).ShouldBe([typeof(HistorySagaAdvanced).ToMessageTypeName()]);
    }

    [Fact]
    public async Task the_history_is_read_a_page_at_a_time()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartHistorySaga(id));
        for (var i = 0; i < 4; i++)
        {
            await Host.InvokeMessageAndWaitAsync(new AdvanceHistorySaga(id));
        }

        var page = await readAsync<HistorySaga>(id, page: 2, pageSize: 2);
        page.TotalCount.ShouldBe(5);
        page.HasMore.ShouldBeTrue();
        page.Events.Select(x => x.Version).ShouldBe([3L, 4L]);
        page.Events.Select(x => x.EventTypeName).ShouldAllBe(x => x == typeof(AdvanceHistorySaga).ToMessageTypeName());

        var last = await readAsync<HistorySaga>(id, page: 3, pageSize: 2);
        last.Events.Count.ShouldBe(1);
        last.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task a_failed_message_is_not_recorded()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartHistorySaga(id));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Host.MessageBus().InvokeAsync(new FailHistorySaga(id), TestContext.Current.CancellationToken));

        // Failed attempts are the saga's dead letters, not its history
        (await readAsync<HistorySaga>(id)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task an_unknown_saga_has_an_empty_history()
    {
        var page = await readAsync<HistorySaga>(newId());
        page.TotalCount.ShouldBe(0);
        page.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_saga_deleted_as_it_completes_takes_its_history_with_it()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartHistorySaga(id));
        await Host.InvokeMessageAndWaitAsync(new AdvanceHistorySaga(id));
        await Host.InvokeMessageAndWaitAsync(new FinishHistorySaga(id));

        (await readAsync<HistorySaga>(id)).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_message_log_cannot_be_read_as_saga_state()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartHistorySaga(id));

        await Should.ThrowAsync<NotSupportedException>(() =>
            history.ReadSagaAtAsync(typeof(HistorySaga).FullName!, id, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task a_retained_saga_keeps_its_history_through_completion_and_late_messages()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartRetainedHistorySaga(id));
        await Host.InvokeMessageAndWaitAsync(new FinishRetainedHistorySaga(id));
        await Host.InvokeMessageAndWaitAsync(new LateRetainedHistoryMessage(id));

        var page = await readAsync<RetainedHistorySaga>(id);
        page.Events.Select(outcomeOf).ShouldBe([
            nameof(SagaHistoryOutcome.Started),
            nameof(SagaHistoryOutcome.Completed),
            nameof(SagaHistoryOutcome.NotFound)
        ]);

        page.Events[1].Data.GetProperty("CompletesSaga").GetBoolean().ShouldBeTrue();
        page.Events[2].Data.GetProperty("Handler").GetString().ShouldBe($"{nameof(RetainedHistorySaga)}.NotFound");
    }

    [Fact]
    public async Task the_retention_sweep_deletes_the_history_with_the_saga()
    {
        var token = TestContext.Current.CancellationToken;

        var open = newId();
        await Host.InvokeMessageAndWaitAsync(new StartRetainedHistorySaga(open));

        var completed = newId();
        await Host.InvokeMessageAndWaitAsync(new StartRetainedHistorySaga(completed));
        await Host.InvokeMessageAndWaitAsync(new FinishRetainedHistorySaga(completed));

        var runtime = Host.GetRuntime();

        // Completed after this cutoff, so kept
        await SagaHistory.SweepAsync(runtime, typeof(RetainedHistorySaga), DateTimeOffset.UtcNow.AddHours(-1), token);
        (await readAsync<RetainedHistorySaga>(completed)).TotalCount.ShouldBe(2);

        var deleted = await SagaHistory.SweepAsync(runtime, typeof(RetainedHistorySaga),
            DateTimeOffset.UtcNow.AddMinutes(1), token);
        deleted.ShouldBeGreaterThanOrEqualTo(2);

        (await readAsync<RetainedHistorySaga>(completed)).TotalCount.ShouldBe(0);
        (await readAsync<RetainedHistorySaga>(open)).TotalCount.ShouldBe(1);
    }
}
