using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.ComplianceTests.Sagas;

// String ids, because RavenDB only stores a saga with a string identity
public record StartCompensatedSaga(string Id);

public record PayCompensatedSaga(string Id, bool RefundFails);

public record CompensatedSagaRefunded(string Id);

public class CompensatedSagaLedgerDownException() : Exception("The ledger is down");

public class CompensatedSagaRefundFailedException() : Exception("The refund failed");

// Kept out of conventional discovery: hosts include it explicitly
[WolverineIgnore]
public class CompensatedSaga : CompensatingSaga
{
    public string Id { get; set; } = string.Empty;

    public static CompensatedSaga Start(StartCompensatedSaga command) => new() { Id = command.Id };

    public void Handle(PayCompensatedSaga command) => throw new CompensatedSagaLedgerDownException();

    public CompensatedSagaRefunded Compensate(PayCompensatedSaga failed, CompensatedSagaLedgerDownException ex)
        => failed.RefundFails ? throw new CompensatedSagaRefundFailedException() : new CompensatedSagaRefunded(Id);
}

[WolverineIgnore]
public static class CompensatedSagaRefunds
{
    public static void Handle(CompensatedSagaRefunded refunded)
    {
    }
}

/// <summary>
/// GH-5556. What every saga storage must do for compensation: keep a <see cref="CompensatingSaga" />'s status and
/// compensation log, saved with the compensation, and record the dead letter's tag where the store has dead letters
/// </summary>
public abstract class SagaCompensationCompliance : IAsyncLifetime
{
    private static readonly string SagaTypeName = typeof(CompensatedSaga).FullName!;

    protected IHost Host { get; private set; } = null!;

    /// <summary>
    /// Configure the saga storage under test. The handler discovery is already restricted to
    /// <see cref="CompensatedSaga" />, the host runs in Solo mode, and every failure goes straight to the error queue
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<CompensatedSaga>()
                    .IncludeType(typeof(CompensatedSagaRefunds));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.OnException<Exception>().MoveToErrorQueue();
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

    private async Task<(string Id, ITrackedSession Session)> payAsync(bool refundFails)
    {
        var id = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new StartCompensatedSaga(id));

        var session = await Host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new PayCompensatedSaga(id, refundFails));

        return (id, session);
    }

    private async Task<JsonElement> stateOfAsync(string id)
    {
        var state = await Host.GetRuntime().SagaStorage.ReadSagaAsync(SagaTypeName, id, TestContext.Current.CancellationToken);
        state.ShouldNotBeNull();
        return state.State;
    }

    private static JsonElement member(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }

        throw new ShouldAssertException($"No {name} in {element}");
    }

    // An enum as its name, whether the storage wrote the name or the number
    private static string enumOf<TEnum>(JsonElement value) where TEnum : struct, Enum
        => value.ValueKind == JsonValueKind.Number ? ((TEnum)(object)value.GetInt32()).ToString() : value.GetString()!;

    private static void shouldHaveCompensation(JsonElement state, SagaStatus status, CompensationOutcome outcome)
    {
        enumOf<SagaStatus>(member(state, nameof(CompensatingSaga.Status))).ShouldBe(status.ToString());

        var log = member(state, nameof(CompensatingSaga.CompensationLog)).EnumerateArray().ToArray();
        var entry = log.ShouldHaveSingleItem();
        member(entry, nameof(CompensationLogEntry.ExceptionType)).GetString()
            .ShouldBe(typeof(CompensatedSagaLedgerDownException).FullName);
        enumOf<CompensationOutcome>(member(entry, nameof(CompensationLogEntry.Outcome))).ShouldBe(outcome.ToString());
    }

    private async Task shouldBeDeadLetteredAsync(string tag)
    {
        var deadLetters = Host.GetRuntime().Storage.DeadLetters;
        if ((deadLetters.Capabilities & DeadLetterCapabilities.Tags) == 0) return;

        var results = await deadLetters.QueryAsync(new DeadLetterEnvelopeQuery { Tag = tag, PageSize = 0 },
            TestContext.Current.CancellationToken);
        results.Envelopes.ShouldContain(x => x.MessageType == typeof(PayCompensatedSaga).ToMessageTypeName());
    }

    [Fact]
    public async Task a_compensated_saga_keeps_its_status_and_log()
    {
        var (id, session) = await payAsync(refundFails: false);

        session.Sent.SingleMessage<CompensatedSagaRefunded>().Id.ShouldBe(id);
        shouldHaveCompensation(await stateOfAsync(id), SagaStatus.Compensated, CompensationOutcome.Compensated);
        await shouldBeDeadLetteredAsync(DeadLetterQueueConstants.CompensatedTag);
    }

    [Fact]
    public async Task a_saga_whose_compensation_failed_keeps_its_status_and_log()
    {
        var (id, session) = await payAsync(refundFails: true);

        session.Sent.MessagesOf<CompensatedSagaRefunded>().ShouldBeEmpty();
        shouldHaveCompensation(await stateOfAsync(id), SagaStatus.CompensationFailed, CompensationOutcome.Failed);
        await shouldBeDeadLetteredAsync(DeadLetterQueueConstants.CompensationFailedTag);
    }
}
