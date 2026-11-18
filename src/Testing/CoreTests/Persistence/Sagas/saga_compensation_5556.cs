using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-5556: saga compensation outside the SagaFaults showcase, on in-memory sagas. The showcase covers the story; these
// cover the rules around it: a plain Saga with Compensate methods, a failure no Compensate matches, a downstream
// Compensate that throws, the retry counts, and how a Compensate method is chosen
public class saga_compensation_5556
{
    private static async Task<IHost> hostAsync(params Type[] types)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var type in types) discovery.IncludeType(type);

                // Retried twice, so three attempts, then compensated and dead lettered
                opts.OnException<Exception>().RetryTimes(2);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static InMemorySagaPersistor sagas(IHost host) => host.Services.GetRequiredService<InMemorySagaPersistor>();

    private static int attempts<T>(ITrackedSession session)
        => session.AllRecordsInOrder(MessageEventType.ExecutionStarted)
            // Not the one-off save that marks a saga CompensationFailed
            .Where(x => x.Message is T && x.Envelope?.Headers.ContainsKey(SagaCompensations.MarkFailedHeader) != true)
            .Select(x => (x.Envelope?.Id, x.AttemptNumber)).Distinct().Count();

    private static string? deadLetterTag<T>(ITrackedSession session)
    {
        var envelope = session.MovedToErrorQueue.Envelopes().Single(x => x.Message is T);
        return envelope.Headers.TryGetValue(DeadLetterQueueConstants.DeadLetterTagHeader, out var tag) ? tag : null;
    }

    [Fact]
    public async Task a_plain_saga_compensates_its_own_failed_handler_after_the_retries()
    {
        using var host = await hostAsync(typeof(PlainParcelSaga), typeof(ParcelSink));
        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartPlainParcel(id));

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new WeighParcel(id));

        attempts<WeighParcel>(session).ShouldBe(3);
        deadLetterTag<WeighParcel>(session).ShouldBe(DeadLetterQueueConstants.CompensatedTag);
        session.Sent.SingleMessage<ParcelReturned>().Id.ShouldBe(id);

        // A plain Saga has no status or log, but its own state is saved with the compensation
        sagas(host).Load<PlainParcelSaga>(id)!.Returned.ShouldBeTrue();
    }

    [Fact]
    public async Task a_failure_no_Compensate_matches_is_dead_lettered_as_before()
    {
        using var host = await hostAsync(typeof(LoggedParcelSaga), typeof(ParcelSink));
        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartLoggedParcel(id));

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new LabelParcel(id, Fails: "printer"));

        attempts<LabelParcel>(session).ShouldBe(3);
        deadLetterTag<LabelParcel>(session).ShouldBeNull();
        session.Sent.MessagesOf<ParcelReturned>().ShouldBeEmpty();

        var saga = sagas(host).Load<LoggedParcelSaga>(id)!;
        saga.Status.ShouldBe(SagaStatus.Active);
        saga.CompensationLog.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_log_records_the_thrown_type_when_a_Compensate_takes_a_base_type()
    {
        using var host = await hostAsync(typeof(LoggedParcelSaga), typeof(ParcelSink));
        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartLoggedParcel(id));

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new LabelParcel(id, Fails: "address"));

        deadLetterTag<LabelParcel>(session).ShouldBe(DeadLetterQueueConstants.CompensatedTag);

        var saga = sagas(host).Load<LoggedParcelSaga>(id)!;
        saga.Status.ShouldBe(SagaStatus.Compensated);
        var entry = saga.CompensationLog.ShouldHaveSingleItem();
        entry.Step.ShouldBe(typeof(LabelParcel).ToMessageTypeName());
        entry.ExceptionType.ShouldBe(typeof(UnknownAddressException).FullName);
        entry.Outcome.ShouldBe(CompensationOutcome.Compensated);
    }

    [Fact]
    public async Task a_compensation_that_fails_once_is_retried_under_the_same_policy()
    {
        using var host = await hostAsync(typeof(LoggedParcelSaga), typeof(ParcelSink));
        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartLoggedParcel(id));
        LoggedParcelSaga.FlakyCompensations[id] = 1;

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new LabelParcel(id, Fails: "address"));

        deadLetterTag<LabelParcel>(session).ShouldBe(DeadLetterQueueConstants.CompensatedTag);
        LoggedParcelSaga.CompensateCalls[id].ShouldBe(2);
        session.Sent.SingleMessage<ParcelReturned>().Id.ShouldBe(id);
        sagas(host).Load<LoggedParcelSaga>(id)!.Status.ShouldBe(SagaStatus.Compensated);
    }

    [Fact]
    public async Task a_compensation_that_keeps_failing_marks_the_saga_and_tags_the_dead_letter()
    {
        using var host = await hostAsync(typeof(LoggedParcelSaga), typeof(ParcelSink));
        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartLoggedParcel(id));
        LoggedParcelSaga.FlakyCompensations[id] = 100;

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new LabelParcel(id, Fails: "address"));

        // Three attempts at the compensation, under the orders' RetryTimes(2)
        LoggedParcelSaga.CompensateCalls[id].ShouldBe(3);
        deadLetterTag<LabelParcel>(session).ShouldBe(DeadLetterQueueConstants.CompensationFailedTag);
        session.Sent.MessagesOf<ParcelReturned>().ShouldBeEmpty();

        var saga = sagas(host).Load<LoggedParcelSaga>(id)!;
        saga.Status.ShouldBe(SagaStatus.CompensationFailed);
        var entry = saga.CompensationLog.ShouldHaveSingleItem();
        entry.ExceptionType.ShouldBe(typeof(UnknownAddressException).FullName);
        entry.Outcome.ShouldBe(CompensationOutcome.Failed);
    }

    [Fact]
    public async Task a_downstream_Compensate_that_throws_dead_letters_the_fault_as_compensation_failed()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<LoggedParcelSaga>().IncludeType(typeof(ParcelSink)).IncludeType(typeof(CourierHandler));

                // The courier's failure comes back to the saga as a Fault<CollectParcel> through a local route
                opts.PublishFaultEvents();
                opts.OnException<Exception>().RetryTimes(2);
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartLoggedParcel(id));

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .WaitForCondition(new DeadLettered<Fault<CollectParcel>>())
            .SendMessageAndWaitAsync(new DispatchParcel(id, CourierFails: true, CompensationFails: true));

        attempts<CollectParcel>(session).ShouldBe(3);
        attempts<Fault<CollectParcel>>(session).ShouldBe(3);
        deadLetterTag<Fault<CollectParcel>>(session).ShouldBe(DeadLetterQueueConstants.CompensationFailedTag);

        var saga = sagas(host).Load<LoggedParcelSaga>(id)!;
        saga.Status.ShouldBe(SagaStatus.CompensationFailed);
        var entry = saga.CompensationLog.ShouldHaveSingleItem();
        entry.Step.ShouldBe(typeof(CollectParcel).ToMessageTypeName());
        entry.ExceptionType.ShouldBe(typeof(CourierUnavailableException).FullName);
        entry.Outcome.ShouldBe(CompensationOutcome.Failed);
    }

    [Fact]
    public async Task a_downstream_failure_is_compensated_from_its_fault()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<LoggedParcelSaga>().IncludeType(typeof(ParcelSink)).IncludeType(typeof(CourierHandler));
                opts.PublishFaultEvents();
                opts.OnException<Exception>().RetryTimes(2);
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartLoggedParcel(id));

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<ParcelReturned>(host)
            .SendMessageAndWaitAsync(new DispatchParcel(id, CourierFails: true, CompensationFails: false));

        session.Sent.SingleMessage<ParcelReturned>().Id.ShouldBe(id);

        var saga = sagas(host).Load<LoggedParcelSaga>(id)!;
        saga.Status.ShouldBe(SagaStatus.Compensated);
        saga.CompensationLog.ShouldHaveSingleItem().ExceptionType.ShouldBe(typeof(CourierUnavailableException).FullName);
    }

    [Fact]
    public async Task a_terminal_failure_skips_the_retries_of_any_handler()
    {
        using var host = await hostAsync(typeof(CourierHandler));

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new CancelCourier("parcel-1"));

        attempts<CancelCourier>(session).ShouldBe(1);
        session.MovedToErrorQueue.SingleMessage<CancelCourier>().ShouldNotBeNull();
    }

    [Fact]
    public async Task a_terminal_failure_keeps_an_explicit_discard()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CourierHandler));
                opts.OnException<CourierClosedException>().Discard();
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new CancelCourier("parcel-2"));

        session.MovedToErrorQueue.MessagesOf<CancelCourier>().ShouldBeEmpty();
        session.Discarded.SingleMessage<CancelCourier>().ShouldNotBeNull();
    }

    [Fact]
    public async Task a_Compensate_method_with_no_exception_parameter_is_refused_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidSagaException>(async () =>
        {
            using var host = await hostAsync(typeof(BadlyCompensatingSaga));
        });

        ex.Message.ShouldContain("Compensate");
    }
}

public class choosing_a_Compensate_method
{
    private static CompensateMethod? select(Type messageType, Exception thrown, bool live)
        => SagaCompensationMethods.For(typeof(ChoosingSaga))
            .Select(messageType, ExceptionInfo.From(thrown), live ? thrown : null);

    [Fact]
    public void the_most_specific_exception_wins()
    {
        select(typeof(LabelParcel), new UnknownAddressException(), live: true)!.ExceptionType
            .ShouldBe(typeof(UnknownAddressException));
    }

    [Fact]
    public void a_base_exception_type_matches_a_subclass()
    {
        select(typeof(LabelParcel), new PrinterJammedException(), live: true)!.ExceptionType
            .ShouldBe(typeof(LabelFailedException));
    }

    [Fact]
    public void a_live_exception_parameter_is_never_matched_for_a_remote_failure()
    {
        var method = select(typeof(LabelParcel), new UnknownAddressException(), live: false)!;
        method.Kind.ShouldBe(CompensateExceptionKind.Remote);
        method.ExceptionType.ShouldBe(typeof(LabelFailedException));
    }

    [Fact]
    public void ExceptionInfo_is_the_fallback()
    {
        select(typeof(LabelParcel), new InvalidOperationException(), live: true)!.Kind
            .ShouldBe(CompensateExceptionKind.Info);
    }

    [Fact]
    public void nothing_matches_another_message()
    {
        select(typeof(WeighParcel), new InvalidOperationException(), live: true).ShouldBeNull();
    }
}

public record StartPlainParcel(Guid Id);

public record WeighParcel(Guid PlainParcelSagaId);

public record ParcelReturned(Guid Id);

public class ScaleBrokenException() : Exception("The scale is broken");

// A plain Saga: Compensate methods work without the CompensatingSaga base type
public class PlainParcelSaga : Saga
{
    public Guid Id { get; set; }
    public bool Returned { get; set; }

    public static PlainParcelSaga Start(StartPlainParcel command) => new() { Id = command.Id };

    public void Handle(WeighParcel command) => throw new ScaleBrokenException();

    public ParcelReturned Compensate(WeighParcel failed, ScaleBrokenException ex)
    {
        Returned = true;
        return new ParcelReturned(Id);
    }
}

public record StartLoggedParcel(Guid Id);

public record LabelParcel(Guid LoggedParcelSagaId, string Fails);

public record DispatchParcel(Guid LoggedParcelSagaId, bool CourierFails, bool CompensationFails);

public record CollectParcel(string Sku, bool Fails);

public record CancelCourier(string Sku);

public class LabelFailedException(string message) : Exception(message);

public class UnknownAddressException() : LabelFailedException("Unknown address");

public class PrinterJammedException() : LabelFailedException("The printer jammed");

public class PrinterOfflineException() : Exception("The printer is offline");

public class CourierUnavailableException() : Exception("No courier is available");

[TerminalFailure]
public class CourierClosedException() : Exception("The courier has closed");

public class CompensationRefusedException() : Exception("The compensation was refused");

public class LoggedParcelSaga : CompensatingSaga
{
    public static readonly Dictionary<Guid, int> FlakyCompensations = new();
    public static readonly Dictionary<Guid, int> CompensateCalls = new();

    public Guid Id { get; set; }
    public bool CompensationFails { get; set; }

    public static LoggedParcelSaga Start(StartLoggedParcel command) => new() { Id = command.Id };

    public void Handle(LabelParcel command)
    {
        if (command.Fails == "address") throw new UnknownAddressException();
        throw new PrinterOfflineException();
    }

    public CollectParcel Handle(DispatchParcel command)
    {
        CompensationFails = command.CompensationFails;
        return new CollectParcel(Id.ToString(), command.CourierFails);
    }

    // Only a LabelFailedException is compensated: a PrinterOfflineException matches nothing
    public ParcelReturned Compensate(LabelParcel failed, LabelFailedException ex)
    {
        lock (CompensateCalls)
        {
            CompensateCalls[Id] = CompensateCalls.GetValueOrDefault(Id) + 1;
            if (FlakyCompensations.TryGetValue(Id, out var remaining) && remaining > 0)
            {
                FlakyCompensations[Id] = remaining - 1;
                throw new CompensationRefusedException();
            }
        }

        return new ParcelReturned(Id);
    }

    public ParcelReturned Compensate(CollectParcel failed, ExceptionInfo ex)
        => CompensationFails ? throw new CompensationRefusedException() : new ParcelReturned(Id);
}

public class BadlyCompensatingSaga : Saga
{
    public Guid Id { get; set; }

    public static BadlyCompensatingSaga Start(StartPlainParcel command) => new() { Id = command.Id };

    public void Compensate(WeighParcel failed)
    {
    }
}

public class ChoosingSaga : Saga
{
    public Guid Id { get; set; }

    public void Compensate(LabelParcel failed, ExceptionInfo ex) { }
    public void Compensate(LabelParcel failed, RemoteFailure<LabelFailedException> ex) { }
    public void Compensate(LabelParcel failed, LabelFailedException ex) { }
    public void Compensate(LabelParcel failed, UnknownAddressException ex) { }
}

public static class CourierHandler
{
    public static void Handle(CollectParcel command)
    {
        if (command.Fails) throw new CourierUnavailableException();
    }

    public static void Handle(CancelCourier command) => throw new CourierClosedException();
}

public static class ParcelSink
{
    public static void Handle(ParcelReturned returned)
    {
    }
}

internal class DeadLettered<T> : ITrackedCondition
{
    private bool _completed;

    public void Record(EnvelopeRecord record)
    {
        if (record.MessageEventType == MessageEventType.MovedToErrorQueue && record.Envelope?.Message is T)
        {
            _completed = true;
        }
    }

    public bool IsCompleted() => _completed;
}
