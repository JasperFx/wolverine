using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;

namespace PostgresqlTests;

// Follow up to GH-5738 / #5739. The sad paths of IDeadLetterInterceptor on a durable endpoint, where the inbox row is
// the only copy of the message: an interceptor that scrubs the body, one that returns no exception, one that throws,
// and a dead-letter write that fails after an interceptor already scrubbed the envelope. Whatever happens, the
// unscrubbed envelope is never written to dead-letter storage, and a message that was not dead lettered runs again
// with its ORIGINAL body.
public class dead_letter_interceptor_sad_paths
{
    private const string SchemaName = "dlq_interceptor_sad_paths";
    private const string CardNumber = "4111 1111 1111 1111";

    public record CardPayment(Guid Id, string CardNumber);

    public class CardDeclined(string cardNumber) : Exception($"Card {cardNumber} was declined");

    public static class CardPaymentHandler
    {
        public static void Handle(CardPayment message) => throw new CardDeclined(message.CardNumber);
    }

    #region sample_redacting_dead_letter_interceptor

    public class CardNumberRedactingInterceptor : IDeadLetterInterceptor
    {
        public ValueTask<Exception?> BeforeStoreAsync(Envelope envelope, Exception? exception,
            CancellationToken cancellation)
        {
            if (envelope.Message is CardPayment payment)
            {
                // Replace the message and clear the serialized body, so dead-letter storage
                // serializes the redacted copy instead of the original
                envelope.Message = payment with { CardNumber = "****" };
                envelope.Data = null;
            }

            // Persist the exception type, but not the message that has the card number in it
            return new ValueTask<Exception?>(new RedactedFailure(exception));
        }
    }

    public class RedactedFailure(Exception? original) : Exception("redacted"), IDeadLetterExceptionInfo
    {
        public string ExceptionType { get; } = original?.GetType().FullName ?? "Unknown";
        public string ExceptionMessage => "redacted";
    }

    #endregion

    private sealed class NullingInterceptor : IDeadLetterInterceptor
    {
        public ValueTask<Exception?> BeforeStoreAsync(Envelope envelope, Exception? exception,
            CancellationToken cancellation) => new((Exception?)null);
    }

    private sealed class ThrowingInterceptor : IDeadLetterInterceptor
    {
        public ValueTask<Exception?> BeforeStoreAsync(Envelope envelope, Exception? exception,
            CancellationToken cancellation)
        {
            // Scrubs first, then fails, so a body left scrubbed would show up in the rescheduled row
            envelope.Message = ((CardPayment)envelope.Message!) with { CardNumber = "****" };
            envelope.Data = null;
            throw new InvalidOperationException("The redaction service is down");
        }
    }

    private static async Task<IHost> startHostAsync(params IDeadLetterInterceptor[] interceptors)
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
        }

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CardPaymentHandler));
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.Policies.UseDurableLocalQueues();
                opts.OnException<CardDeclined>().MoveToErrorQueue();

                foreach (var interceptor in interceptors)
                {
                    opts.Services.AddSingleton<IDeadLetterInterceptor>(interceptor);
                }

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    private static async Task<DeadLetterEnvelope?> theDeadLetterAsync(IHost host)
    {
        var results = await host.GetRuntime().Storage.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(),
            TestContext.Current.CancellationToken);
        return results.Envelopes.SingleOrDefault();
    }

    private static async Task<CardPayment> theRescheduledMessageAsync(IHost host)
    {
        Envelope? row = null;
        for (var i = 0; i < 50 && row == null; i++)
        {
            row = (await host.GetRuntime().Storage.Admin.AllIncomingAsync())
                .SingleOrDefault(x => x.MessageType == typeof(CardPayment).ToMessageTypeName()
                                      && x.Status == EnvelopeStatus.Scheduled);
            if (row == null) await Task.Delay(100);
        }

        row.ShouldNotBeNull("the envelope was neither dead lettered nor handed back to be executed again");
        row.OwnerId.ShouldBe(TransportConstants.AnyNode);

        return (CardPayment)host.GetRuntime().Options.DefaultSerializer.ReadFromData(typeof(CardPayment), row);
    }

    // The envelope is handed back rather than finished with, so a tracked session would wait on it forever
    private static Task sendUntrackedAsync(IHost host, Guid id)
    {
        return host.MessageBus().SendAsync(new CardPayment(id, CardNumber)).AsTask();
    }

    private static Task sendAsync(IHost host, Guid id)
    {
        return host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(15.Seconds())
            .SendMessageAndWaitAsync(new CardPayment(id, CardNumber));
    }

    [Fact]
    public async Task an_interceptor_can_scrub_the_stored_body()
    {
        using var host = await startHostAsync(new CardNumberRedactingInterceptor());
        var id = Guid.NewGuid();

        await sendAsync(host, id);

        var deadLetter = (await theDeadLetterAsync(host)).ShouldNotBeNull();
        deadLetter.ExceptionType.ShouldBe(typeof(CardDeclined).FullName);
        deadLetter.ExceptionMessage.ShouldBe("redacted");

        // Stored as the serialized body, so read it back the way a replay would
        var stored = (CardPayment)host.GetRuntime().Options.DefaultSerializer
            .ReadFromData(typeof(CardPayment), deadLetter.Envelope);
        stored.Id.ShouldBe(id);
        stored.CardNumber.ShouldBe("****");

        deadLetter.Envelope.Headers.Values.ShouldNotContain(v => v != null && v.Contains(CardNumber));
    }

    [Fact]
    public async Task an_interceptor_that_returns_no_exception_leaves_no_exception_text()
    {
        using var host = await startHostAsync(new NullingInterceptor());

        await sendAsync(host, Guid.NewGuid());

        var deadLetter = (await theDeadLetterAsync(host)).ShouldNotBeNull();
        deadLetter.ExceptionMessage.ShouldBeNullOrEmpty();

        var headers = deadLetter.Envelope.Headers;
        headers.ContainsKey(DeadLetterQueueConstants.ExceptionTypeHeader).ShouldBeFalse();
        headers.ContainsKey(DeadLetterQueueConstants.ExceptionMessageHeader).ShouldBeFalse();
        headers.ContainsKey(DeadLetterQueueConstants.ExceptionStackHeader).ShouldBeFalse();
        headers.Values.ShouldNotContain(v => v != null && v.Contains(CardNumber));

        // The failure group still comes from the original exception
        headers.ContainsKey(DeadLetterQueueConstants.FailureGroupHeader).ShouldBeTrue();
    }

    [Fact]
    public async Task interceptors_run_in_order_and_chain_the_exception()
    {
        var recorder = new RecordingInterceptor();
        using var host = await startHostAsync(new CardNumberRedactingInterceptor(), recorder);

        await sendAsync(host, Guid.NewGuid());

        recorder.Received.ShouldBeOfType<RedactedFailure>();
        recorder.Message.ShouldBeOfType<CardPayment>().CardNumber.ShouldBe("****");
        (await theDeadLetterAsync(host)).ShouldNotBeNull().ExceptionMessage.ShouldBe("redacted");
    }

    [Fact]
    public async Task an_interceptor_that_throws_dead_letters_nothing_and_hands_back_the_original()
    {
        using var host = await startHostAsync(new ThrowingInterceptor());
        var id = Guid.NewGuid();

        await sendUntrackedAsync(host, id);

        var rescheduled = await theRescheduledMessageAsync(host);
        rescheduled.Id.ShouldBe(id);
        rescheduled.CardNumber.ShouldBe(CardNumber, "the message that runs again is the original, not a scrubbed copy");

        (await theDeadLetterAsync(host)).ShouldBeNull("the unscrubbed envelope must not reach dead-letter storage");
    }

    [Fact]
    public async Task a_failed_dead_letter_write_hands_back_the_original_body()
    {
        using var host = await startHostAsync(new CardNumberRedactingInterceptor());
        var id = Guid.NewGuid();

        // Make the dead-letter write itself fail
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"drop table {SchemaName}.{DatabaseConstants.DeadLetterTable}";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await sendUntrackedAsync(host, id);

        var rescheduled = await theRescheduledMessageAsync(host);
        rescheduled.Id.ShouldBe(id);
        rescheduled.CardNumber.ShouldBe(CardNumber,
            "rescheduling rewrites the inbox row from the envelope, which must not be the redacted copy");
    }

    private sealed class RecordingInterceptor : IDeadLetterInterceptor
    {
        public Exception? Received { get; private set; }
        public object? Message { get; private set; }

        public ValueTask<Exception?> BeforeStoreAsync(Envelope envelope, Exception? exception,
            CancellationToken cancellation)
        {
            Received = exception;
            Message = envelope.Message;
            return new ValueTask<Exception?>(exception);
        }
    }
}
