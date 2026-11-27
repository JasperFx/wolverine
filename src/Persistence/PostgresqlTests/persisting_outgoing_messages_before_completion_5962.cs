using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Wolverine.Util;

namespace PostgresqlTests;

/// <summary>
///     GH-5962. A handler with no transactional middleware stores the messages it cascades to durable
///     destinations after it returns. A store that refuses one of them used to log it as discarded and still mark
///     the source message handled, so the cascade was simply gone. With
///     <see cref="DurabilitySettings.PersistOutgoingMessagesBeforeCompletion" /> the handler's durable messages are
///     stored together, before any is sent, and a refusal is a failure of the source message that its error
///     policies handle.
/// </summary>
public class persisting_outgoing_messages_before_completion_5962 : IAsyncLifetime
{
    private const string Schema = "cascades_before_completion";
    private IHost _host = null!;

    public ValueTask InitializeAsync()
    {
        CascadeSourceHandler.Attempts = 0;
        StoredCascadeHandler.Handled = 0;
        RefusedCascadeHandler.Handled = 0;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private async Task startAsync(bool persistBeforeCompletion)
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(Schema);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                if (persistBeforeCompletion)
                {
                    #region sample_persist_outgoing_messages_before_completion

                    // Store every message a handler sends to a durable destination before any of them is sent,
                    // and treat a failure to store them as a failure of the message being handled
                    opts.Durability.PersistOutgoingMessagesBeforeCompletion = true;

                    #endregion
                }

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);
                opts.Policies.UseDurableLocalQueues();

                opts.PublishMessage<CascadeSource>().ToLocalQueue("source");
                opts.PublishMessage<StoredCascade>().ToLocalQueue("stored");
                opts.PublishMessage<RefusedCascade>().ToLocalQueue("refused");

                // The error policies of the source message decide what a refused store means
                opts.OnException<PostgresException>().RetryOnce().Then.MoveToErrorQueue();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CascadeSourceHandler))
                    .IncludeType(typeof(StoredCascadeHandler))
                    .IncludeType(typeof(RefusedCascadeHandler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await executeAsync($"""
            create function {Schema}.refuse_envelope() returns trigger language plpgsql as $$
            begin
                raise exception 'the message store refuses this envelope';
            end $$;
            create trigger refuse_cascade before insert on {Schema}.wolverine_incoming_envelopes
                for each row when (new.message_type = '{typeof(RefusedCascade).ToMessageTypeName()}')
                execute function {Schema}.refuse_envelope();
            """);
    }

    private Task publishAndWaitAsync(CascadeSource source)
    {
        return _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .IgnoreFailureAcks()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(_ => _host.MessageBus().PublishAsync(source).AsTask());
    }

    [Fact]
    public async Task without_the_option_the_refused_cascade_is_lost_and_the_source_is_handled()
    {
        await startAsync(false);

        await publishAndWaitAsync(new CascadeSource(Guid.NewGuid(), IncludeRefused: true));

        // Today's behavior, kept as the default: the source succeeded, its stored cascade went out, and the
        // refused one left nothing behind
        CascadeSourceHandler.Attempts.ShouldBe(1);
        StoredCascadeHandler.Handled.ShouldBe(1);
        (await countDeadLettersAsync<CascadeSource>()).ShouldBe(0);
        (await countIncomingAsync<RefusedCascade>()).ShouldBe(0);
    }

    [Fact]
    public async Task a_refused_cascade_fails_the_source_through_its_error_policies_and_nothing_is_sent()
    {
        await startAsync(true);

        await publishAndWaitAsync(new CascadeSource(Guid.NewGuid(), IncludeRefused: true));

        // RetryOnce, then the error queue: the handler ran twice, and the source is a dead letter
        CascadeSourceHandler.Attempts.ShouldBe(2);
        (await countDeadLettersAsync<CascadeSource>()).ShouldBe(1);

        // All or nothing: the cascade the store would have taken was not stored, or sent, either
        StoredCascadeHandler.Handled.ShouldBe(0);
        (await countIncomingAsync<StoredCascade>()).ShouldBe(0);
        (await countIncomingAsync<RefusedCascade>()).ShouldBe(0);
    }

    [Fact]
    public async Task a_retry_that_finds_the_store_recovered_sends_every_cascade_once()
    {
        await startAsync(true);

        // The handler drops the trigger on its second attempt
        await publishAndWaitAsync(new CascadeSource(Guid.NewGuid(), IncludeRefused: true, RecoverOnRetry: true));

        CascadeSourceHandler.Attempts.ShouldBe(2);
        StoredCascadeHandler.Handled.ShouldBe(1);
        RefusedCascadeHandler.Handled.ShouldBe(1);
        (await countDeadLettersAsync<CascadeSource>()).ShouldBe(0);
    }

    [Fact]
    public async Task stored_cascades_are_forwarded_without_being_stored_twice()
    {
        await startAsync(true);

        await publishAndWaitAsync(new CascadeSource(Guid.NewGuid(), IncludeRefused: false, IncludeScheduled: true));

        CascadeSourceHandler.Attempts.ShouldBe(1);

        // A second insert of the same envelope would have been refused as a duplicate and never handled
        StoredCascadeHandler.Handled.ShouldBe(1);

        // The scheduled cascade is one inbox row, waiting for its time
        (await countIncomingAsync<ScheduledCascade>("Scheduled")).ShouldBe(1);
    }

    [Fact]
    public async Task an_invoked_handler_throws_to_its_caller_when_its_cascades_are_refused()
    {
        await startAsync(true);

        await Should.ThrowAsync<PostgresException>(() =>
            _host.MessageBus().InvokeAsync(new CascadeSource(Guid.NewGuid(), IncludeRefused: true)));

        // The inline retry ran the handler a second time before giving up
        CascadeSourceHandler.Attempts.ShouldBe(2);
        (await countIncomingAsync<StoredCascade>()).ShouldBe(0);
    }

    private static Task<long> countIncomingAsync<T>(string? status = null)
    {
        var sql =
            $"select count(*) from {Schema}.wolverine_incoming_envelopes where message_type = '{typeof(T).ToMessageTypeName()}'";
        if (status != null) sql += $" and status = '{status}'";
        return countAsync(sql);
    }

    private static Task<long> countDeadLettersAsync<T>()
    {
        return countAsync(
            $"select count(*) from {Schema}.wolverine_dead_letters where message_type = '{typeof(T).ToMessageTypeName()}'");
    }

    internal static async Task executeAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }

    private static async Task<long> countAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        return (long)(await new NpgsqlCommand(sql, conn).ExecuteScalarAsync())!;
    }

    public record CascadeSource(
        Guid Id,
        bool IncludeRefused,
        bool RecoverOnRetry = false,
        bool IncludeScheduled = false);

    public record StoredCascade(Guid Id);

    public record RefusedCascade(Guid Id);

    public record ScheduledCascade(Guid Id);

    public static class CascadeSourceHandler
    {
        public static int Attempts;

        public static async Task<OutgoingMessages> Handle(CascadeSource source, Envelope envelope)
        {
            Interlocked.Increment(ref Attempts);

            if (source.RecoverOnRetry && envelope.Attempts > 1)
            {
                await executeAsync($"drop trigger if exists refuse_cascade on {Schema}.wolverine_incoming_envelopes");
            }

            var messages = new OutgoingMessages { new StoredCascade(source.Id) };
            if (source.IncludeRefused) messages.Add(new RefusedCascade(source.Id));
            if (source.IncludeScheduled) messages.Delay(new ScheduledCascade(source.Id), 1.Hours());

            return messages;
        }
    }

    public static class StoredCascadeHandler
    {
        public static int Handled;

        public static void Handle(StoredCascade message) => Interlocked.Increment(ref Handled);
    }

    public static class RefusedCascadeHandler
    {
        public static int Handled;

        public static void Handle(RefusedCascade message) => Interlocked.Increment(ref Handled);

        public static void Handle(ScheduledCascade message)
        {
        }
    }
}
