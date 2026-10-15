using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Pubsub.Http;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
///     GH-5048. Every push redelivery is mapped into a new envelope, so the attempt count has to come from the
///     <c>deliveryAttempt</c> Pub/Sub puts in a push request for a subscription with a dead letter policy. The
///     emulator never sends it, so these push crafted request bodies
/// </summary>
public class pubsub_push_delivery_attempt_5048 : IAsyncLifetime
{
    private WebApplication _app = default!;
    private HttpClient _client = default!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AlwaysFailingPushHandler));
            opts.Services.AddSingleton<PushAttemptRecorder>();

            opts.UsePubsub("wolverine");
            opts.ListenToPubsubTopic("failing").DefaultIncomingMessage<AlwaysFailingPush>();

            // Requeued twice, then dead lettered on the third attempt
            opts.OnException<RequeuedPushException>().Requeue(3);

            // Retried by the broker after each delay, then dead lettered on the third attempt
            opts.OnException<ScheduledPushException>().ScheduleRetry(1.Seconds(), 5.Seconds());

            opts.StubAllExternalTransports();
        });

        _app = builder.Build();
        _app.MapPubsubPushReceiver("/pubsub/push", opts => opts.AllowAnonymous());

        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private PushAttemptRecorder recorder => _app.Services.GetRequiredService<PushAttemptRecorder>();

    private static object pushBody(string failure, string messageId, int? deliveryAttempt)
    {
        var message = new
        {
            data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new AlwaysFailingPush(failure, messageId))),
            messageId,
            publishTime = "2026-10-15T12:00:00.000Z"
        };

        const string subscription = "projects/wolverine/subscriptions/failing";

        // Pub/Sub only adds deliveryAttempt when the subscription has a dead letter policy
        return deliveryAttempt.HasValue
            ? new { message, subscription, deliveryAttempt = deliveryAttempt.Value }
            : new { message, subscription };
    }

    private async Task<HttpStatusCode> push(string failure, string messageId, int? deliveryAttempt)
    {
        var response = await _client.PostAsJsonAsync("/pubsub/push", pushBody(failure, messageId, deliveryAttempt),
            TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task the_mapped_envelope_starts_from_the_attempts_before_this_delivery()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(pushBody("requeue", "7001", 4)));

        var envelope = (await new PubsubPushEnvelopeMapper(_app.Services.GetRequiredService<IWolverineRuntime>())
            .MapAsync(context, TestContext.Current.CancellationToken)).Single();

        // Handling the message counts the fourth attempt
        envelope.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task without_a_delivery_attempt_the_envelope_is_a_first_attempt()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(pushBody("requeue", "7002", null)));

        var envelope = (await new PubsubPushEnvelopeMapper(_app.Services.GetRequiredService<IWolverineRuntime>())
            .MapAsync(context, TestContext.Current.CancellationToken)).Single();

        envelope.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task a_requeue_policy_escalates_to_dead_letter_storage_on_its_last_attempt()
    {
        (await push("requeue", "7101", 1)).ShouldBe(HttpStatusCode.InternalServerError);
        (await push("requeue", "7101", 2)).ShouldBe(HttpStatusCode.InternalServerError);

        // Requeue(3) is exhausted, so the message is moved to Wolverine's dead letter storage and acked
        (await push("requeue", "7101", 3)).ShouldBe(HttpStatusCode.OK);

        recorder.AttemptsFor("7101").ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task a_scheduled_retry_policy_escalates_to_dead_letter_storage_on_its_last_attempt()
    {
        (await push("schedule", "7201", 1)).ShouldBe(HttpStatusCode.InternalServerError);
        (await push("schedule", "7201", 2)).ShouldBe(HttpStatusCode.InternalServerError);
        (await push("schedule", "7201", 3)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task without_a_delivery_attempt_every_redelivery_is_a_first_attempt()
    {
        // The documented limitation: with no count from the broker the policy never escalates, so configure the
        // push subscription's own dead letter policy
        (await push("requeue", "7301", null)).ShouldBe(HttpStatusCode.InternalServerError);
        (await push("requeue", "7301", null)).ShouldBe(HttpStatusCode.InternalServerError);
        (await push("requeue", "7301", null)).ShouldBe(HttpStatusCode.InternalServerError);

        recorder.AttemptsFor("7301").ShouldBe([1, 1, 1]);
    }
}

public record AlwaysFailingPush(string Failure, string Key);

public class RequeuedPushException : Exception;

public class ScheduledPushException : Exception;

public class PushAttemptRecorder
{
    private readonly List<(string MessageId, int Attempts)> _attempts = new();

    public void Record(string messageId, int attempts)
    {
        lock (_attempts) _attempts.Add((messageId, attempts));
    }

    public int[] AttemptsFor(string messageId)
    {
        lock (_attempts) return _attempts.Where(x => x.MessageId == messageId).Select(x => x.Attempts).ToArray();
    }
}

// Kept out of the conventional discovery the other Pub/Sub tests use. The push host includes it explicitly
[WolverineIgnore]
public class AlwaysFailingPushHandler
{
    public static void Handle(AlwaysFailingPush message, Envelope envelope, PushAttemptRecorder recorder)
    {
        recorder.Record(message.Key, envelope.Attempts);

        throw message.Failure == "schedule" ? new ScheduledPushException() : new RequeuedPushException();
    }
}
