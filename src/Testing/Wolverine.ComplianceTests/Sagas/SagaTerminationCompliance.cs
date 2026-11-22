using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.Sagas;

// String ids, because RavenDB only stores a saga with a string identity
public record StartTerminableSaga(string Id);

public record TouchTerminableSaga(string Id);

/// <summary>GH-5839. A saga for completing from outside its handlers.</summary>
public class TerminableSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public int Touches { get; set; }

    public static TerminableSaga Start(StartTerminableSaga command) => new() { Id = command.Id };

    public void Handle(TouchTerminableSaga command) => Touches++;

    public static void NotFound(TouchTerminableSaga command) => NotFoundIds.Add(command.Id);

    public static readonly ConcurrentBag<string> NotFoundIds = new();
}

/// <summary>
/// GH-5839. What every saga storage must do for <see cref="ISagaStoreDiagnostics.CompleteSagaAsync" />, through
/// <c>IWolverineRuntime.SagaStorage</c>, against a real host: an operator completes one saga instance from outside its
/// handlers, and the saga is deleted as <c>MarkCompleted()</c> would delete it.
/// </summary>
public abstract class SagaTerminationCompliance : IAsyncLifetime
{
    private static readonly string TerminableName = typeof(TerminableSaga).FullName!;

    /// <summary>The saga types under test, for storages that register saga types (<c>AddSagaType</c>)</summary>
    public static readonly Type[] SagaTypes = [typeof(TerminableSaga)];

    protected IHost Host { get; private set; } = null!;

    /// <summary>
    /// Configure the saga storage under test. The handler discovery is already restricted to <see cref="SagaTypes" />,
    /// and the host runs in Solo mode.
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<TerminableSaga>();
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

    private static string newId() => Guid.NewGuid().ToString();

    private Task<SagaTerminationResult> completeAsync(string sagaTypeName, string id)
        => sagas.CompleteSagaAsync(sagaTypeName, id, "stuck waiting on a partner that went away",
            TestContext.Current.CancellationToken);

    private async Task<int?> touchesAsync(string sagaTypeName, string id)
    {
        var state = await sagas.ReadSagaAsync(sagaTypeName, id, TestContext.Current.CancellationToken);
        if (state == null) return null;

        foreach (var property in state.State.EnumerateObject())
        {
            if (property.Name.Equals(nameof(TerminableSaga.Touches), StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetInt32();
            }
        }

        return 0;
    }

    [Fact]
    public void completion_is_offered_for_the_saga_type()
    {
        sagas.SupportsSagaCompletion(TerminableName).ShouldBeTrue();
        sagas.SupportsSagaCompletion(nameof(TerminableSaga)).ShouldBeTrue();
        sagas.SupportsSagaCompletion("No.Such.Saga").ShouldBeFalse();
    }

    [Fact]
    public async Task completing_a_saga_deletes_it()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartTerminableSaga(id));
        (await sagas.ReadSagaAsync(TerminableName, id, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        var result = await completeAsync(TerminableName, id);

        result.Outcome.ShouldBe(SagaTerminationOutcome.Deleted);
        result.Terminated.ShouldBeTrue();
        result.SagaTypeName.ShouldBe(TerminableName);
        result.Identity.ShouldBe(id);
        (await sagas.ReadSagaAsync(TerminableName, id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task completing_a_saga_by_its_short_type_name()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartTerminableSaga(id));

        var result = await completeAsync(nameof(TerminableSaga), id);

        result.Outcome.ShouldBe(SagaTerminationOutcome.Deleted);
        (await sagas.ReadSagaAsync(TerminableName, id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task completing_a_deleted_saga_again_finds_nothing()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartTerminableSaga(id));
        await completeAsync(TerminableName, id);

        var again = await completeAsync(TerminableName, id);

        again.Outcome.ShouldBe(SagaTerminationOutcome.NotFound);
        again.Terminated.ShouldBeFalse();
    }

    [Fact]
    public async Task a_message_for_a_deleted_saga_goes_to_NotFound()
    {
        var id = newId();
        await Host.InvokeMessageAndWaitAsync(new StartTerminableSaga(id));
        await completeAsync(TerminableName, id);

        await Host.InvokeMessageAndWaitAsync(new TouchTerminableSaga(id));

        TerminableSaga.NotFoundIds.ShouldContain(id);
    }

    [Fact]
    public async Task an_unknown_saga_is_not_found()
    {
        var result = await completeAsync(TerminableName, newId());

        result.Outcome.ShouldBe(SagaTerminationOutcome.NotFound);
        result.Terminated.ShouldBeFalse();
    }

    [Fact]
    public async Task a_reason_is_required()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            sagas.CompleteSagaAsync(TerminableName, newId(), " ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task an_unknown_saga_type_is_refused()
    {
        await Should.ThrowAsync<NotSupportedException>(() =>
            sagas.CompleteSagaAsync("No.Such.Saga", newId(), "because", TestContext.Current.CancellationToken));
    }
}
