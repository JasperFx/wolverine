using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Xunit;
namespace Wolverine.Nats.Tests;

/// <summary>
/// Regression coverage for GH-3676: a durable JetStream consumer auto-provisioned by the listener
/// was created with no <c>FilterSubject</c>, so several durable consumers sharing one stream each
/// received every message published to that stream instead of only their own subject's.
///
/// The subject filter was only ever applied to *ephemeral* consumers, while the resource-provisioning
/// path in <c>NatsEndpoint</c> always set it for named consumers — these tests pin both the consumer
/// configuration the server ends up with and the delivery behavior that depends on it.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsJetStreamConsumerFilterTests
{
    private readonly ITestOutputHelper _output;

    public NatsJetStreamConsumerFilterTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task auto_provisioned_durable_consumers_are_filtered_to_their_own_subject()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var id = Guid.NewGuid().ToString("N");
        var stream = $"FILTER_{id}";
        var root = $"filter.{id}";
        var subjectA = $"{root}.type-a";
        var subjectB = $"{root}.type-b";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s => s.WithSubjects($"{root}.>"));

                opts.Policies.DisableConventionalLocalRouting();

                opts.ListenToNatsSubject(subjectA).UseJetStream(stream, $"consumer-a-{id}");
                opts.ListenToNatsSubject(subjectB).UseJetStream(stream, $"consumer-b-{id}");
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await using var connection = new NatsConnection(new NatsOpts { Url = natsUrl });
        await connection.ConnectAsync();
        var js = connection.CreateJetStreamContext();

        // Each durable consumer must be scoped to the subject its listener was bound to.
        // Before the fix both came back with a null FilterSubject
        var consumerA = await js.GetConsumerAsync(stream, $"consumer-a-{id}", TestContext.Current.CancellationToken);
        consumerA.Info.Config.FilterSubject.ShouldBe(subjectA);

        var consumerB = await js.GetConsumerAsync(stream, $"consumer-b-{id}", TestContext.Current.CancellationToken);
        consumerB.Info.Config.FilterSubject.ShouldBe(subjectB);
    }

    [Fact]
    public async Task a_scheduling_enabled_consumer_also_owns_its_schedule_subject()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var id = Guid.NewGuid().ToString("N");
        var stream = $"FILTERSCHED_{id}";
        var root = $"filtersched.{id}";
        var subject = $"{root}.receiver";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineWorkQueueStream(stream, s => s.EnableScheduledDelivery(), $"{root}.>");

                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToNatsSubject(subject).UseJetStream(stream, $"sched-consumer-{id}");
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var transport = host.Services.GetRequiredService<IWolverineRuntime>()
            .Options.Transports.GetOrCreate<NatsTransport>();
        if (!transport.ServerSupportsScheduledSend) return;

        await using var connection = new NatsConnection(new NatsOpts { Url = natsUrl });
        await connection.ConnectAsync();
        var js = connection.CreateJetStreamContext();

        // Native scheduling publishes its control message to "{subject}.scheduled". No single NATS
        // filter covers both that and "{subject}", and on a work queue stream a control message no
        // consumer covers is discarded, so the scheduled send silently never arrives. The consumer
        // therefore has to carry both subjects
        var consumer = await js.GetConsumerAsync(stream, $"sched-consumer-{id}", TestContext.Current.CancellationToken);
        consumer.Info.Config.FilterSubjects.ShouldBe([subject, $"{subject}.scheduled"]);
    }

    [Fact]
    public async Task a_message_only_reaches_the_consumer_whose_subject_matches()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var id = Guid.NewGuid().ToString("N");
        var stream = $"FILTERDELIVERY_{id}";
        var root = $"filterdelivery.{id}";
        var subjectA = $"{root}.type-a";
        var subjectB = $"{root}.type-b";

        FilteredMessageHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                // A plain limits stream, not a work queue: work-queue retention deletes a message
                // on the first ack, which would mask a second consumer pulling the same message
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s => s.WithSubjects($"{root}.>"));

                opts.Policies.DisableConventionalLocalRouting();

                opts.ListenToNatsSubject(subjectA).UseJetStream(stream, $"delivery-a-{id}");
                opts.ListenToNatsSubject(subjectB).UseJetStream(stream, $"delivery-b-{id}");

                opts.PublishMessage<FilteredMessage>().ToNatsSubject(subjectA).UseJetStream(stream)
                    .SendInline();
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await host.MessageBus().SendAsync(new FilteredMessage(id));

        // Only the type-a consumer is subscribed to this subject. With an unfiltered
        // consumer-b the same message was delivered a second time (GH-3676)
        await FilteredMessageHandler.WaitForFirstAsync();
        (await FilteredMessageHandler.SettledCountAsync()).ShouldBe(1);
    }
}

public record FilteredMessage(string Id);

[WolverineHandler]
public static class FilteredMessageHandler
{
    private static int _count;
    private static TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        _first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Handle(FilteredMessage message)
    {
        Interlocked.Increment(ref _count);
        _first.TrySetResult();
    }

    public static Task WaitForFirstAsync()
    {
        return _first.Task.WaitAsync(30.Seconds());
    }

    /// <summary>
    /// Give a duplicate delivery from a second, wrongly-unfiltered consumer time to land before
    /// counting — a bare count immediately after the first handler run would pass either way
    /// </summary>
    public static async Task<int> SettledCountAsync()
    {
        await Task.Delay(3.Seconds());
        return Volatile.Read(ref _count);
    }
}
