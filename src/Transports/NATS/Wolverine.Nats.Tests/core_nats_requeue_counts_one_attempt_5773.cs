using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Shouldly;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-5773. Core NATS requeues by republishing from inside the listener's <c>_defer</c> RetryBlock, and
/// <c>RepublishAsync</c> incremented <c>Attempts</c> on every try. A publish that failed and was retried made one
/// requeue count as two or three attempts, so <c>Requeue(maxAttempts)</c> and other attempt-bounded policies fired
/// early. One requeue now counts once however many times its publish is retried.
/// </summary>
public class core_nats_requeue_counts_one_attempt_5773
{
    [Fact]
    public async Task a_requeue_whose_publish_is_retried_counts_one_attempt()
    {
        // Nothing listens on this port, so every publish fails the way a dropped connection does
        await using var connection = new NatsConnection(new NatsOpts
        {
            Url = "nats://127.0.0.1:1",
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            MaxReconnectRetry = 0
        });

        var endpoint = new NatsTransport().EndpointForSubject("requeue5773");
        var subscriber = new CoreNatsSubscriber(endpoint, connection, NullLogger<NatsEndpoint>.Instance,
            new NatsIncomingMapping(endpoint, new NatsEnvelopeMapper(endpoint), null));

        var envelope = new NatsEnvelope(null, null) { Data = [1, 2, 3], Attempts = 1 };

        // The RetryBlock retries the same envelope after each failure
        for (var i = 0; i < 3; i++)
        {
            await Should.ThrowAsync<Exception>(() => subscriber.RepublishAsync(envelope, CancellationToken.None));
        }

        envelope.Attempts.ShouldBe(2);
    }
}

/// <summary>
/// GH-5773. The ordinary requeue, whose first publish succeeds, still counts exactly one attempt and carries it on
/// the republished message.
/// </summary>
[Collection("NATS Integration")]
public class core_nats_requeue_counts_one_attempt_on_the_wire_5773(NatsContainerFixture fixture)
{
    [Fact]
    public async Task a_successful_requeue_counts_one_attempt_and_sends_it()
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = fixture.ConnectionString });
        await connection.ConnectAsync();
        var cancellation = TestContext.Current.CancellationToken;

        var subject = "requeue5773." + Guid.NewGuid().ToString("N");
        await using var sub = await connection.SubscribeCoreAsync<byte[]>(subject, cancellationToken: cancellation);
        await connection.PingAsync(cancellation);

        var endpoint = new NatsTransport().EndpointForSubject(subject);
        var subscriber = new CoreNatsSubscriber(endpoint, connection, NullLogger<NatsEndpoint>.Instance,
            new NatsIncomingMapping(endpoint, new NatsEnvelopeMapper(endpoint), null));

        var envelope = new NatsEnvelope(null, null) { Data = [1, 2, 3], Attempts = 1 };

        await subscriber.RepublishAsync(envelope, CancellationToken.None);

        envelope.Attempts.ShouldBe(2);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await sub.Msgs.ReadAsync(timeout.Token);
        received.Headers![EnvelopeConstants.AttemptsKey].ToString().ShouldBe("2");
    }
}
