using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using IntegrationTests;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Marten;
using Wolverine.Marten;
using Wolverine.Http.Transport.Push;
using Wolverine.Postgresql;
using Wolverine.Pubsub.Http;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
///     PR #5044 exploratory testing. A real Pub/Sub emulator push subscription delivering to a
///     DurabilityMode.ServerlessWithOutbox host through MapPubsubPushReceiver, with a normal (Solo) Wolverine host
///     publishing to the topic through its default batching sender
/// </summary>
public class pubsub_push_end_to_end_exploratory_5044
{
    private readonly ITestOutputHelper _output;

    public pubsub_push_end_to_end_exploratory_5044(ITestOutputHelper output)
    {
        _output = output;
    }

    private static CancellationToken ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- a. single + batched delivery

    [Fact]
    public async Task a_single_and_batched_messages_are_each_handled_exactly_once()
    {
        await using var h = await PushHarness.StartAsync(_output, dedup: false);

        var bus = h.Publisher.MessageBus();
        await bus.PublishAsync(new ExploreOrder(h.Run, 0));
        await h.WaitForAsync(() => h.Hits("order").Count >= 1, 20.Seconds());

        const int count = 60;
        for (var i = 1; i <= count; i++) await bus.PublishAsync(new ExploreOrder(h.Run, i));

        await h.WaitForAsync(() => h.Hits("order").Select(x => x.Seq).Distinct().Count() >= count + 1, 30.Seconds());
        await Task.Delay(3.Seconds(), ct); // let any duplicate show up

        var hits = h.Hits("order");
        h.Dump();

        var seqs = hits.Select(x => x.Seq).ToList();
        _output.WriteLine($"handled order sequence: {string.Join(",", seqs)}");
        _output.WriteLine($"push requests: {h.Requests.Count}, batched: {h.Requests.Count(x => x.Batched)}, statuses: {string.Join(",", h.Requests.Select(x => x.Status))}");

        seqs.Distinct().Count().ShouldBe(count + 1);
        seqs.Count.ShouldBe(count + 1, "every message handled exactly once");
        h.Requests.ShouldAllBe(x => x.Batched, "a Wolverine BatchedSender marks every Pub/Sub message batched=1");
        h.Requests.Count.ShouldBeLessThan(count + 1, "several envelopes should have been packed into a Pub/Sub message");
    }

    // ---------------------------------------------------------------- b. transient failure in a batch

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task b_a_transient_failure_in_the_middle_of_a_batch_is_redelivered(bool dedup)
    {
        await using var h = await PushHarness.StartAsync(_output, dedup);

        // Exactly what PubsubSenderProtocol publishes for a batch of three
        await h.PublishWolverineBatchAsync(
            new ExploreFlaky(h.Run, 1, 0),
            new ExploreFlaky(h.Run, 2, 1), // fails on its first attempt only
            new ExploreFlaky(h.Run, 3, 0));

        await h.WaitForAsync(() => h.Hits("flaky").Any(x => x.Seq == 3 && x.Succeeded), 30.Seconds());
        await Task.Delay(3.Seconds(), ct);
        h.Dump();

        var succeeded = h.Hits("flaky").Where(x => x.Succeeded).Select(x => x.Seq).ToList();
        _output.WriteLine($"dedup={dedup} successful handling order: {string.Join(",", succeeded)}");
        _output.WriteLine($"statuses: {string.Join(",", h.Requests.Select(x => x.Status))}");

        h.Requests.Select(x => x.Status).ShouldBe([500, 200]);
        succeeded.Count(x => x == 2).ShouldBe(1);
        succeeded.Count(x => x == 3).ShouldBe(1);

        // Documented: without DeduplicateWithInbox the envelopes ahead of the failure run again on redelivery
        succeeded.Count(x => x == 1).ShouldBe(dedup ? 1 : 2);
    }

    // ---------------------------------------------------------------- b'. a poison message and the attempt count

    [Fact]
    public async Task b2_a_message_that_always_fails_with_requeue_then_dead_letter_policy()
    {
        // opts.OnException<ExplorePoisonException>().Requeue(2) => after two requeues the default policy should
        // move it to the dead letter storage. Each push redelivery is a fresh envelope though
        await using var h = await PushHarness.StartAsync(_output, dedup: false);

        await h.PublishWolverineBatchAsync(new ExplorePoison(h.Run));

        await Task.Delay(10.Seconds(), ct);
        h.Dump();

        var hits = h.Hits("poison");
        _output.WriteLine($"poison handled {hits.Count} times, attempts seen: {string.Join(",", hits.Select(x => x.Attempts))}");
        var deadLetters = await h.CountAsync("wolverine_dead_letters");
        _output.WriteLine($"dead letter rows: {deadLetters}; statuses {string.Join(",", h.Requests.Select(x => x.Status))}");

        // LEFT FAILING ON PURPOSE. Expectation from the error policy: at most 3 tries, then dead lettered and acked.
        // Observed: every push redelivery is a fresh envelope with Attempts reset, so Requeue(2) never escalates
        // and the emulator redelivers about once a second forever (no deliveryAttempt in the emulator's push body)
        hits.Count.ShouldBeLessThanOrEqualTo(3, "Requeue(2) should give up after its attempts and dead letter the message");
        deadLetters.ShouldBe(1);
    }

    // ---------------------------------------------------------------- c. slower than the ack deadline

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task c_a_handler_slower_than_the_ack_deadline(bool dedup)
    {
        await using var h = await PushHarness.StartAsync(_output, dedup, ackDeadlineSeconds: 10);

        await h.PublishWolverineBatchAsync(new ExploreSlow(h.Run, 14));

        await Task.Delay(40.Seconds(), ct);
        h.Dump();

        var hits = h.Hits("slow");
        _output.WriteLine($"dedup={dedup}: slow handler started {hits.Count} times, max concurrency {ExploreLog.MaxConcurrency(h.Run)}");
        _output.WriteLine($"statuses: {string.Join(",", h.Requests.Select(x => $"{x.Status}@{x.Elapsed.TotalSeconds:F1}s"))}");

        // DESIGN QUESTION, recorded as observed against the emulator: the push is redelivered every ack deadline
        // while the first request is still running, and the late 200 does not ack it. Without deduplication the
        // handler runs again every 10 seconds, forever. DeduplicateWithInbox only records a message once it has
        // been handled, so the redelivery that arrives while the first run is in flight still runs concurrently;
        // only the one after the first run finished is skipped as a duplicate
        ExploreLog.MaxConcurrency(h.Run).ShouldBe(2);
        if (dedup)
        {
            hits.Count.ShouldBe(2);
        }
        else
        {
            hits.Count.ShouldBeGreaterThanOrEqualTo(3);
        }
    }

    // ---------------------------------------------------------------- d. cascading to another Pub/Sub topic

    [Fact]
    public async Task d_a_cascaded_message_is_published_before_the_200_and_leaves_no_outbox_row()
    {
        await using var h = await PushHarness.StartAsync(_output, dedup: false, pullCascadeInMiddleware: true);

        await h.Publisher.MessageBus().PublishAsync(new ExploreCascade(h.Run));

        await h.WaitForAsync(() => h.Requests.Any(x => x.Status == 200), 20.Seconds());
        h.Dump();

        var request = h.Requests.First(x => x.Status == 200);
        _output.WriteLine($"cascade messages already on the topic when the push request finished: {request.CascadePulledBeforeResponse}");
        var outgoing = await h.CountAsync("wolverine_outgoing_envelopes");
        var incoming = await h.CountAsync("wolverine_incoming_envelopes");
        _output.WriteLine($"outbox rows {outgoing}, inbox rows {incoming}");

        request.CascadePulledBeforeResponse.ShouldBe(1);
        outgoing.ShouldBe(0);
    }

    // ---------------------------------------------------------------- e. a non-Wolverine publisher

    [Fact]
    public async Task d2_with_the_marten_transactional_outbox_the_outbox_row_is_deleted_after_the_inline_send()
    {
        await using var h = await PushHarness.StartAsync(_output, dedup: false, pullCascadeInMiddleware: true, marten: true);

        await h.Publisher.MessageBus().PublishAsync(new ExploreCascadeInTransaction(h.Run));

        await h.WaitForAsync(() => h.Requests.Any(x => x.Status == 200), 20.Seconds());
        h.Dump();

        var request = h.Requests.First(x => x.Status == 200);
        var outgoing = await h.CountAsync("wolverine_outgoing_envelopes");
        _output.WriteLine($"cascade pulled before response: {request.CascadePulledBeforeResponse}, outbox rows {outgoing}");

        h.Hits("cascade-tx").Count.ShouldBe(1);
        request.CascadePulledBeforeResponse.ShouldBe(1);
        outgoing.ShouldBe(0);
    }

    [Fact]
    public async Task e_third_party_json_through_DefaultIncomingMessage()
    {
        await using var h = await PushHarness.StartAsync(_output, dedup: true);

        await h.PublishThirdPartyAsync(h.ThirdPartyTopic, """{"Run":"RUN","Sku":"T-65"}""".Replace("RUN", h.Run));
        await h.PublishThirdPartyAsync(h.ThirdPartyTopic, """{"Run":"RUN","Sku":"X-34"}""".Replace("RUN", h.Run));

        await h.WaitForAsync(() => h.Hits("thirdparty").Count >= 2, 20.Seconds());
        await Task.Delay(2.Seconds(), ct);
        h.Dump();

        var hits = h.Hits("thirdparty");
        hits.Select(x => x.Text).OrderBy(x => x).ShouldBe(["T-65", "X-34"]);
        hits.Select(x => x.EnvelopeId).Distinct().Count().ShouldBe(2);
        h.Requests.ShouldAllBe(x => x.Status == 200);

        // DeduplicateWithInbox recorded both under the derived ids
        (await h.CountAsync("wolverine_incoming_envelopes")).ShouldBe(2);
    }

    // ---------------------------------------------------------------- f. surprises

    [Fact]
    public async Task f1_an_unknown_subscription_receives_wolverine_batches_and_third_party_json()
    {
        await using var h = await PushHarness.StartAsync(_output, dedup: false);

        // A push subscription Wolverine knows nothing about, on the Wolverine topic and on the third party topic
        await h.CreatePushSubscriptionAsync(h.OrdersTopic, $"explore5044-unknown-{h.Run}", 10);
        await h.CreatePushSubscriptionAsync(h.ThirdPartyTopic, $"explore5044-unknown3p-{h.Run}", 10);

        await h.PublishWolverineBatchAsync(new ExploreOrder(h.Run, 7));
        await h.PublishThirdPartyAsync(h.ThirdPartyTopic, """{"Run":"RUN","Sku":"U-1"}""".Replace("RUN", h.Run));

        await Task.Delay(8.Seconds(), ct);
        h.Dump();

        _output.WriteLine($"statuses: {string.Join(",", h.Requests.Select(x => $"{x.Subscription}={x.Status}"))}");
        _output.WriteLine($"dead letters: {await h.CountAsync("wolverine_dead_letters")}");

        // Known + unknown subscription both handle the Wolverine message
        h.Hits("order").Count(x => x.Seq == 7).ShouldBe(2);
    }

    [Fact]
    public async Task f2_a_push_subscription_in_a_tenant_project_stamps_the_tenant()
    {
        await using var h = await PushHarness.StartAsync(_output, dedup: false);

        await h.CreateTopicAsync(h.ThirdPartyTopic, PushHarness.TenantProject);
        await h.CreatePushSubscriptionAsync(h.ThirdPartyTopic, h.ThirdPartyTopic, 10, PushHarness.TenantProject);

        await h.PublishThirdPartyAsync(h.ThirdPartyTopic, """{"Run":"RUN","Sku":"TEN"}""".Replace("RUN", h.Run),
            PushHarness.TenantProject);

        await h.WaitForAsync(() => h.Hits("thirdparty").Count >= 1, 20.Seconds());
        h.Dump();

        h.Hits("thirdparty").Single().TenantId.ShouldBe("explore-tenant");
    }

    [Fact]
    public async Task f3_auto_provisioning_on_the_serverless_host_creates_a_pull_subscription_observed()
    {
        // A serverless host that also says AutoProvision() for its "listener" endpoint creates the subscription as
        // a PULL subscription, so the push subscription the push receiver needs is never there unless created
        // outside Wolverine first
        await using var h = await PushHarness.StartAsync(_output, dedup: false, autoProvisionServerless: true,
            createOrdersPushSubscription: false);

        var subscription = await h.Subscriber.GetSubscriptionAsync(new SubscriptionName(PushHarness.Project, h.OrdersTopic));
        _output.WriteLine($"push endpoint after AutoProvision: '{subscription.PushConfig?.PushEndpoint}'");

        // DESIGN QUESTION, as observed: AutoProvision() provisions the ListenToPubsubTopic() subscription as a PULL
        // subscription even under ServerlessWithOutbox, where nothing will ever pull it
        (subscription.PushConfig?.PushEndpoint ?? "").ShouldBeEmpty();
    }
}

// ==================================================================================== harness

internal record PushRequest(string Subscription, bool Batched, int Status, TimeSpan Elapsed, int CascadePulledBeforeResponse);

internal class PushHarness : IAsyncDisposable
{
    public const string Project = "explore5044";
    public const string TenantProject = "explore5044-tenant";

    private readonly ITestOutputHelper _output;
    private readonly List<SubscriptionName> _subscriptions = new();
    private readonly List<TopicName> _topics = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private PushHarness(ITestOutputHelper output)
    {
        _output = output;
        Run = Guid.NewGuid().ToString("N")[..8];

        Publisher = null!;
        Serverless = null!;
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", TestingExtensions.EmulatorHost);

        PublisherClient = new PublisherServiceApiClientBuilder { EmulatorDetection = EmulatorDetection.EmulatorOnly }.Build();
        Subscriber = new SubscriberServiceApiClientBuilder { EmulatorDetection = EmulatorDetection.EmulatorOnly }.Build();
    }

    public string Run { get; }
    public string OrdersTopic => $"explore5044-orders-{Run}";
    public string ThirdPartyTopic => $"explore5044-3p-{Run}";
    public string CascadeTopic => $"explore5044-cascade-{Run}";
    public string CascadePull => $"explore5044-cascade-pull-{Run}";

    public IHost Publisher { get; private set; }
    public WebApplication Serverless { get; private set; }
    public PublisherServiceApiClient PublisherClient { get; }
    public SubscriberServiceApiClient Subscriber { get; }
    public int Port { get; private set; }

    public ConcurrentQueue<PushRequest> RequestQueue { get; } = new();
    public IReadOnlyList<PushRequest> Requests => RequestQueue.ToArray();

    public bool PullCascadeInMiddleware { get; private set; }

    public static async Task<PushHarness> StartAsync(ITestOutputHelper output, bool dedup, int ackDeadlineSeconds = 10,
        bool pullCascadeInMiddleware = false, bool autoProvisionServerless = false, bool createOrdersPushSubscription = true,
        bool marten = false)
    {
        var h = new PushHarness(output) { PullCascadeInMiddleware = pullCascadeInMiddleware };
        await h.cleanDatabaseAsync();

        await h.CreateTopicAsync(h.OrdersTopic);
        await h.CreateTopicAsync(h.ThirdPartyTopic);
        await h.CreateTopicAsync(h.CascadeTopic);
        await h.createPullSubscriptionAsync(h.CascadeTopic, h.CascadePull);

        await h.startServerlessAsync(dedup, autoProvisionServerless, marten);

        if (createOrdersPushSubscription)
        {
            // The subscription ids are the topic names, which is the subscription Wolverine's ListenToPubsubTopic()
            // endpoint for that topic knows
            await h.CreatePushSubscriptionAsync(h.OrdersTopic, h.OrdersTopic, ackDeadlineSeconds);
            await h.CreatePushSubscriptionAsync(h.ThirdPartyTopic, h.ThirdPartyTopic, ackDeadlineSeconds);
        }

        h.Publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "explore5044-publisher";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
                opts.UsePubsub(Project).UseEmulatorDetection(EmulatorDetection.EmulatorOnly);
                opts.PublishAllMessages().ToPubsubTopic(h.OrdersTopic);
            }).StartAsync();

        return h;
    }

    private async Task startServerlessAsync(bool dedup, bool autoProvision, bool marten)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Host.UseWolverine(opts =>
        {
            opts.ServiceName = "explore5044-serverless";
            opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ExplorePushHandlers));
            if (marten)
            {
                // The transactional outbox: the cascaded message is written to the outbox in the handler's
                // Marten transaction, then sent inline before the response
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "explore5044_pubsub";
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "explore5044_pubsub");
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.IncludeType(typeof(ExploreMartenHandlers));
            }
            else
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "explore5044_pubsub");
            }

            var pubsub = opts.UsePubsub(Project).UseEmulatorDetection(EmulatorDetection.EmulatorOnly);
            pubsub.AddTenant("explore-tenant", TenantProject);
            if (autoProvision) pubsub.AutoProvision();

            opts.ListenToPubsubTopic(OrdersTopic);
            opts.ListenToPubsubTopic(ThirdPartyTopic).DefaultIncomingMessage<ExploreThirdParty>();
            opts.PublishMessage<ExploreCascaded>().ToPubsubTopic(CascadeTopic);

            opts.OnException<ExploreTransientException>().Requeue(5);
            opts.OnException<ExplorePoisonException>().Requeue(2);
        });

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var started = _clock.Elapsed;
            context.Request.EnableBuffering();
            var subscription = "";
            var batched = false;
            try
            {
                using var doc = await JsonDocument.ParseAsync(context.Request.Body);
                subscription = doc.RootElement.GetProperty("subscription").GetString()!.Split('/').Last();
                batched = doc.RootElement.GetProperty("message").TryGetProperty("attributes", out var attrs) &&
                          attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("batched", out _);
            }
            catch
            {
                // not a push
            }

            context.Request.Body.Position = 0;
            await next();

            var pulled = PullCascadeInMiddleware ? await pullCascadeAsync() : -1;
            RequestQueue.Enqueue(new PushRequest(subscription, batched, context.Response.StatusCode,
                _clock.Elapsed - started, pulled));
        });

        app.MapPubsubPushReceiver("/pubsub/push", opts =>
        {
            opts.AllowAnonymous();
            opts.DeduplicateWithInbox = dedup;
        });

        await app.StartAsync();
        Serverless = app;

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    private async Task<int> pullCascadeAsync()
    {
        try
        {
            var response = await Subscriber.PullAsync(
                new PullRequest { SubscriptionAsSubscriptionName = new SubscriptionName(Project, CascadePull), MaxMessages = 10 },
                CallSettings.FromExpiration(Expiration.FromTimeout(TimeSpan.FromSeconds(3))));

            if (response.ReceivedMessages.Count > 0)
            {
                await Subscriber.AcknowledgeAsync(new SubscriptionName(Project, CascadePull),
                    response.ReceivedMessages.Select(x => x.AckId));
            }

            return response.ReceivedMessages.Count;
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.DeadlineExceeded)
        {
            return 0;
        }
    }

    public async Task CreateTopicAsync(string topic, string project = Project)
    {
        var name = new TopicName(project, topic);
        try
        {
            await PublisherClient.CreateTopicAsync(name);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.AlreadyExists)
        {
        }

        _topics.Add(name);
    }

    private async Task createPullSubscriptionAsync(string topic, string subscription)
    {
        var name = new SubscriptionName(Project, subscription);
        await Subscriber.CreateSubscriptionAsync(new Subscription
        {
            SubscriptionName = name,
            TopicAsTopicName = new TopicName(Project, topic),
            AckDeadlineSeconds = 10
        });
        _subscriptions.Add(name);
    }

    public async Task CreatePushSubscriptionAsync(string topic, string subscription, int ackDeadlineSeconds,
        string project = Project)
    {
        var name = new SubscriptionName(project, subscription);
        await Subscriber.CreateSubscriptionAsync(new Subscription
        {
            SubscriptionName = name,
            TopicAsTopicName = new TopicName(project, topic),
            AckDeadlineSeconds = ackDeadlineSeconds,
            PushConfig = new PushConfig { PushEndpoint = $"http://host.docker.internal:{Port}/pubsub/push" }
        });
        _subscriptions.Add(name);
    }

    /// <summary>
    ///     Publish exactly the one Pub/Sub message a Wolverine BatchedSender (PubsubSenderProtocol) publishes for
    ///     these messages
    /// </summary>
    public async Task PublishWolverineBatchAsync(params object[] messages)
    {
        var runtime = Publisher.Services.GetRequiredService<IWolverineRuntime>();
        var endpoint = runtime.Options.Transports.GetOrCreate<PubsubTransport>().Topics[OrdersTopic];

        var envelopes = messages.Select(m => new Envelope(m)
        {
            Data = JsonSerializer.SerializeToUtf8Bytes(m, m.GetType()),
            ContentType = "application/json",
            Destination = endpoint.Uri
        }).ToList();

        var message = new PubsubMessage();
        new PubsubEnvelopeMapper(endpoint).MapOutgoingToMessage(new OutgoingMessageBatch(endpoint.Uri, envelopes), message);

        await PublisherClient.PublishAsync(new TopicName(Project, OrdersTopic), [message]);
    }

    public async Task PublishThirdPartyAsync(string topic, string json, string project = Project)
    {
        await PublisherClient.PublishAsync(new TopicName(project, topic),
            [new PubsubMessage { Data = ByteString.CopyFromUtf8(json) }]);
    }

    public IReadOnlyList<ExploreHit> Hits(string kind) => ExploreLog.For(Run).Where(x => x.Kind == kind).ToList();

    public async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        Dump();
        throw new TimeoutException("Condition not met in " + timeout);
    }

    public void Dump()
    {
        foreach (var r in Requests)
            _output.WriteLine($"REQ sub={r.Subscription} batched={r.Batched} status={r.Status} elapsed={r.Elapsed.TotalMilliseconds:F0}ms cascadePulled={r.CascadePulledBeforeResponse}");
        foreach (var hit in ExploreLog.For(Run))
            _output.WriteLine($"HIT {hit.Kind} seq={hit.Seq} id={hit.EnvelopeId} attempts={hit.Attempts} tenant={hit.TenantId} ok={hit.Succeeded} text={hit.Text} at={hit.At:HH:mm:ss.fff}");
    }

    public async Task<long> CountAsync(string table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from explore5044_pubsub.{table}";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task cleanDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "drop schema if exists explore5044_pubsub cascade";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Delete the push subscriptions first, as the emulator would otherwise redeliver forever to a dead port
        foreach (var subscription in _subscriptions)
        {
            try { await Subscriber.DeleteSubscriptionAsync(subscription); } catch { }
        }

        foreach (var topic in _topics)
        {
            try { await PublisherClient.DeleteTopicAsync(topic); } catch { }
        }

        if (Publisher != null)
        {
            await Publisher.StopAsync();
            Publisher.Dispose();
        }

        if (Serverless != null)
        {
            await Serverless.StopAsync();
            await Serverless.DisposeAsync();
        }
    }
}

public record ExploreOrder(string Run, int Seq);

public record ExploreFlaky(string Run, int Seq, int FailTimes);

public record ExplorePoison(string Run);

public record ExploreSlow(string Run, int Seconds);

public record ExploreCascade(string Run);

public record ExploreCascaded(string Run);

public record ExploreCascadeInTransaction(string Run);

public class ExploreDoc
{
    public string Id { get; set; } = "";
}

public record ExploreThirdParty(string Run, string Sku);

public class ExploreTransientException : Exception;

public class ExplorePoisonException : Exception;

public record ExploreHit(string Kind, int Seq, Guid EnvelopeId, int Attempts, string? TenantId, bool Succeeded, string? Text, DateTime At);

public static class ExploreLog
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<ExploreHit>> _hits = new();
    private static readonly ConcurrentDictionary<string, int> _inflight = new();
    private static readonly ConcurrentDictionary<string, int> _maxConcurrency = new();
    private static readonly ConcurrentDictionary<string, int> _attempts = new();

    public static IReadOnlyList<ExploreHit> For(string run) =>
        _hits.TryGetValue(run, out var q) ? q.ToArray() : [];

    public static void Add(string run, string kind, int seq, Envelope envelope, bool succeeded, string? text = null)
    {
        _hits.GetOrAdd(run, _ => new ConcurrentQueue<ExploreHit>())
            .Enqueue(new ExploreHit(kind, seq, envelope.Id, envelope.Attempts, envelope.TenantId, succeeded, text, DateTime.Now));
    }

    public static int NextAttempt(string key) => _attempts.AddOrUpdate(key, 1, (_, x) => x + 1);

    public static void Enter(string run)
    {
        var now = _inflight.AddOrUpdate(run, 1, (_, x) => x + 1);
        _maxConcurrency.AddOrUpdate(run, now, (_, x) => Math.Max(x, now));
    }

    public static void Exit(string run) => _inflight.AddOrUpdate(run, 0, (_, x) => x - 1);

    public static int MaxConcurrency(string run) => _maxConcurrency.TryGetValue(run, out var x) ? x : 0;
}

[WolverineIgnore]
public class ExplorePushHandlers
{
    public static void Handle(ExploreOrder message, Envelope envelope)
        => ExploreLog.Add(message.Run, "order", message.Seq, envelope, true);

    public static void Handle(ExploreFlaky message, Envelope envelope)
    {
        var attempt = ExploreLog.NextAttempt($"{message.Run}/{message.Seq}");
        if (attempt <= message.FailTimes)
        {
            ExploreLog.Add(message.Run, "flaky", message.Seq, envelope, false);
            throw new ExploreTransientException();
        }

        ExploreLog.Add(message.Run, "flaky", message.Seq, envelope, true);
    }

    public static void Handle(ExplorePoison message, Envelope envelope)
    {
        ExploreLog.Add(message.Run, "poison", 0, envelope, false);
        throw new ExplorePoisonException();
    }

    public static async Task Handle(ExploreSlow message, Envelope envelope)
    {
        ExploreLog.Enter(message.Run);
        ExploreLog.Add(message.Run, "slow", 0, envelope, true, "start");
        try
        {
            await Task.Delay(message.Seconds.Seconds());
        }
        finally
        {
            ExploreLog.Exit(message.Run);
        }
    }

    public static ExploreCascaded Handle(ExploreCascade message, Envelope envelope)
    {
        ExploreLog.Add(message.Run, "cascade", 0, envelope, true);
        return new ExploreCascaded(message.Run);
    }

    public static void Handle(ExploreThirdParty message, Envelope envelope)
        => ExploreLog.Add(message.Run, "thirdparty", 0, envelope, true, message.Sku);
}

[WolverineIgnore]
public class ExploreMartenHandlers
{
    public static ExploreCascaded Handle(ExploreCascadeInTransaction message, Envelope envelope, IDocumentSession session)
    {
        session.Store(new ExploreDoc { Id = message.Run });
        ExploreLog.Add(message.Run, "cascade-tx", 0, envelope, true);
        return new ExploreCascaded(message.Run);
    }
}
