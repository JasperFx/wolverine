using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Nats.Internal;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// NATS.Net returns a JetStream publish the server refused (stream full under <c>DiscardNew</c>, a failed
/// <c>Nats-Expected-*</c> check) as a <see cref="PubAckResponse"/> with <c>Error</c> set rather than throwing.
/// <see cref="JetStreamPublisher"/> has to turn that into an exception, or the sending agent reports the
/// envelope as sent and a durable outbox deletes it although the stream never stored it. A duplicate
/// <c>Nats-Msg-Id</c> is the opposite case: the stream already holds the message, so it stays a success.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsJetStreamPublishRejectionTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsJetStreamPublishRejectionTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task publish_rejected_by_full_discard_new_stream_throws()
    {
        var (stream, subject) = uniqueNames("FULL");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [subject])
        {
            MaxMsgs = 1,
            Discard = StreamConfigDiscard.New
        }, TestContext.Current.CancellationToken);

        var publisher = new JetStreamPublisher(connection, js, NullLogger<NatsEndpoint>.Instance);

        // Fills the stream
        await publishAsync(publisher, subject, new Envelope());

        var exception = await Should.ThrowAsync<NatsJSApiException>(
            () => publishAsync(publisher, subject, new Envelope()));
        _output.WriteLine($"{exception.Error.Code}/{exception.Error.ErrCode}: {exception.Error.Description}");
        exception.Error.Description.ShouldNotBeNull().ShouldContain("maximum messages");

        (await countAsync(js, stream)).ShouldBe(1);
    }

    [Fact]
    public async Task expected_last_sequence_mismatch_throws()
    {
        var (stream, subject) = uniqueNames("EXPECTED");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [subject]), TestContext.Current.CancellationToken);

        var publisher = new JetStreamPublisher(connection, js, NullLogger<NatsEndpoint>.Instance);

        // The stream is empty, so its last sequence is 0, not 42
        var headers = new NatsHeaders { ["Nats-Expected-Last-Sequence"] = "42" };

        var exception = await Should.ThrowAsync<NatsJSApiException>(
            () => publishAsync(publisher, subject, new Envelope(), headers));
        _output.WriteLine($"{exception.Error.Code}/{exception.Error.ErrCode}: {exception.Error.Description}");
        exception.Error.Description.ShouldNotBeNull().ShouldContain("wrong last sequence");

        (await countAsync(js, stream)).ShouldBe(0);
    }

    [Fact]
    public async Task duplicate_msg_id_is_not_an_error()
    {
        var (stream, subject) = uniqueNames("DUPLICATE");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [subject])
        {
            DuplicateWindow = 5.Minutes()
        }, TestContext.Current.CancellationToken);

        var publisher = new JetStreamPublisher(connection, js, NullLogger<NatsEndpoint>.Instance);

        // The same envelope Id is the same Nats-Msg-Id: a resend after a lost publish ack has to stay a
        // success, or a durable outbox would retry a message the stream already holds forever
        var envelope = new Envelope();
        await publishAsync(publisher, subject, envelope);
        await publishAsync(publisher, subject, envelope);

        (await countAsync(js, stream)).ShouldBe(1);
    }

    [Fact]
    public async Task scheduled_publish_rejected_by_the_stream_throws()
    {
        var (stream, subject) = uniqueNames("SCHEDULED");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();

        // A scheduling stream cannot use DiscardNew, so let the stream refuse the control message by size
        await js.CreateStreamAsync(new StreamConfig(stream, [subject, subject + ".scheduled"])
        {
            MaxMsgSize = 512,
            AllowMsgSchedules = true
        }, TestContext.Current.CancellationToken);

        var publisher = new JetStreamPublisher(connection, js, NullLogger<NatsEndpoint>.Instance);

        // The native scheduling control message goes through the same JetStream publish
        var scheduled = new Envelope { ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5) };
        var exception = await Should.ThrowAsync<NatsJSApiException>(() => publisher.PublishAsync(subject,
            new byte[1024], new NatsHeaders(), null, scheduled, TestContext.Current.CancellationToken).AsTask());
        _output.WriteLine($"{exception.Error.Code}/{exception.Error.ErrCode}: {exception.Error.Description}");

        (await countAsync(js, stream)).ShouldBe(0);
    }

    [Fact]
    public async Task rejected_publish_stays_in_the_durable_outbox_until_the_stream_accepts_it()
    {
        var (stream, subject) = uniqueNames("OUTBOX");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [subject])
        {
            MaxMsgs = 1,
            Discard = StreamConfigDiscard.New
        }, TestContext.Current.CancellationToken);

        // Another producer has filled the stream
        await js.PublishAsync(subject, new byte[] { 1 }, cancellationToken: TestContext.Current.CancellationToken);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PubAckOutbox";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseNats(_fixture.ConnectionString);
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nats_puback");

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream).UseDurableOutbox();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var store = host.Services.GetRequiredService<IMessageStore>();
        await store.Admin.ClearAllAsync();

        await host.MessageBus().PublishAsync(new OrderPlaced("rejected-at-first"));

        // The stream refuses the publish, so the outbox row must survive the send attempts
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);
        (await store.Admin.AllOutgoingAsync()).Count.ShouldBe(1);
        (await countAsync(js, stream)).ShouldBe(1);

        // Once the stream has room again the outbox delivers the message and only then lets go of it
        await js.PurgeStreamAsync(stream, new StreamPurgeRequest(), TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline &&
               ((await store.Admin.AllOutgoingAsync()).Count > 0 || await countAsync(js, stream) == 0))
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        (await countAsync(js, stream)).ShouldBe(1);
        (await store.Admin.AllOutgoingAsync()).ShouldBeEmpty();
    }

    private static (string Stream, string Subject) uniqueNames(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ($"PUBACK_{prefix}_{suffix}", $"puback.{prefix.ToLowerInvariant()}.{suffix}");
    }

    private async Task<NatsConnection> connectAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        return connection;
    }

    private static Task publishAsync(JetStreamPublisher publisher, string subject, Envelope envelope,
        NatsHeaders? headers = null)
    {
        return publisher.PublishAsync(subject, [1, 2, 3], headers ?? new NatsHeaders(), null, envelope,
            TestContext.Current.CancellationToken).AsTask();
    }

    private static async Task<long> countAsync(INatsJSContext js, string stream)
    {
        var info = await js.GetStreamAsync(stream);
        return info.Info.State.Messages;
    }
}
