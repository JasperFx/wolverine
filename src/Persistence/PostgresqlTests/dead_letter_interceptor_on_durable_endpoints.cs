using IntegrationTests;
using JasperFx.Core.Reflection;
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
using Wolverine.Tracking;
using Wolverine.Transports;

namespace PostgresqlTests;

// A durable endpoint is its own dead letter queue, so its dead letters used to skip IDeadLetterInterceptor and
// reach dead letter storage with the exception text the interceptor exists to scrub
public class dead_letter_interceptor_on_durable_endpoints
{
    private const string SchemaName = "dlq_interceptor_durable";
    private const string Secret = "card number 4111 1111 1111 1111";

    [Fact]
    public async Task the_stored_dead_letter_carries_the_replacement_exception()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(SchemaName, TestContext.Current.CancellationToken);
        }

        var interceptor = new RedactingDeadLetterInterceptor();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LeakingMessageHandler));
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.Policies.UseDurableLocalQueues();
                opts.OnException<LeakingException>().MoveToErrorQueue();
                opts.Services.AddSingleton<IDeadLetterInterceptor>(interceptor);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new LeakingMessage());

        var results = await host.GetRuntime().Storage.DeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery(), TestContext.Current.CancellationToken);
        var deadLetter = results.Envelopes.ShouldHaveSingleItem();

        deadLetter.ExceptionType.ShouldBe(typeof(LeakingException).FullNameInCode());
        deadLetter.ExceptionMessage.ShouldBe(RedactedLeakingException.Redacted);

        var headers = deadLetter.Envelope.Headers;
        headers[DeadLetterQueueConstants.ExceptionTypeHeader].ShouldBe(typeof(LeakingException).FullNameInCode());
        headers[DeadLetterQueueConstants.ExceptionMessageHeader].ShouldBe(RedactedLeakingException.Redacted);
        headers.GetValueOrDefault(DeadLetterQueueConstants.ExceptionStackHeader).ShouldBeNullOrEmpty();
        headers.Values.ShouldNotContain(value => value != null && value.Contains(Secret));

        var original = interceptor.Original.ShouldBeOfType<LeakingException>();

        // The replacement has no stack trace, so on its own it would fall into one "no stack trace" group
        var group = new DefaultDeadLetterFingerprinter().Fingerprint(deadLetter.Envelope, original).Truncated().Group;
        deadLetter.FailureGroup.ShouldBe(group);
        headers[DeadLetterQueueConstants.FailureGroupHeader].ShouldBe(group);
    }

    public record LeakingMessage;

    public class LeakingException() : Exception(Secret);

    public static class LeakingMessageHandler
    {
        public static void Handle(LeakingMessage message) => throw new LeakingException();
    }

    private sealed class RedactedLeakingException(Exception original) : Exception(Redacted), IDeadLetterExceptionInfo
    {
        public const string Redacted = "redacted";

        public string ExceptionType { get; } = original.GetType().FullNameInCode();
        public string ExceptionMessage => Redacted;
    }

    private sealed class RedactingDeadLetterInterceptor : IDeadLetterInterceptor
    {
        public Exception? Original { get; private set; }

        public ValueTask<Exception?> BeforeStoreAsync(Envelope envelope, Exception? exception,
            CancellationToken cancellation)
        {
            Original = exception;
            return new ValueTask<Exception?>(new RedactedLeakingException(exception!));
        }
    }
}
