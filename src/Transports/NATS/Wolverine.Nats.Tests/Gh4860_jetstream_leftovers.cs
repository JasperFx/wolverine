using System.Text;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-4860: the items #4856 and #4859 left out. The two bugs -- a leaked connection on every re-run of
/// ConnectAsync, and a dead letter copy NATS.Net refuses because the stack trace header carries a carriage
/// return -- and the two missing pieces of configuration: mirror / sources / placement on a declared stream,
/// and a last word over the JetStream publish options.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class Gh4860_jetstream_leftovers
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public Gh4860_jetstream_leftovers(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ---- connection leak ---------------------------------------------------------------------

    [Fact]
    public async Task re_running_connect_on_a_live_transport_reuses_its_connection()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Leftovers";
                opts.UseNats(_fixture.ConnectionString);
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();

        var first = transport.Connection;
        first.ConnectionState.ShouldBe(NatsConnectionState.Open);

        // BrokerResource re-runs this at the start of every operation -- AutoProvision's resource setup does
        // so right after start-up, while the listeners and senders hold this connection -- so a live
        // connection has to be kept, not replaced
        await transport.ConnectAsync(runtime);

        transport.Connection.ShouldBeSameAs(first);
        isDisposed(first).ShouldBeFalse();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_failed_connect_is_disposed_before_the_next_attempt_opens_again()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Leftovers";
                opts.UseNats(_fixture.ConnectionString);
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // A second transport, pointed at nothing, driven the way the host drives one on a failed start
        var unreachable = new NatsTransport("unreachable");
        unreachable.Configuration.ConnectionString = "nats://127.0.0.1:1";
        unreachable.Configuration.ConfigureNatsOpts = o => o with { ConnectTimeout = 1.Seconds() };

        await Should.ThrowAsync<Exception>(() => unreachable.ConnectAsync(runtime).AsTask());
        var first = unreachable.Connection;
        first.ConnectionState.ShouldBe(NatsConnectionState.Closed);

        // The retry used to open a second connection on top of this one and leave it behind
        await Should.ThrowAsync<Exception>(() => unreachable.ConnectAsync(runtime).AsTask());

        unreachable.Connection.ShouldNotBeSameAs(first);
        isDisposed(first).ShouldBeTrue("the previous attempt's connection was not disposed");

        await unreachable.DisposeAsync();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // ---- dead letter copy with a carriage return in a header ---------------------------------

    [Fact]
    public async Task a_dead_letter_copy_whose_failure_metadata_holds_a_carriage_return_is_still_forwarded()
    {
        var natsUrl = _fixture.ConnectionString;
        var id = Guid.NewGuid().ToString("N");
        var stream = $"DLQCRLF_{id}";
        var subject = $"dlqcrlf.{id}.incoming";
        var deadLetterSubject = $"dlqcrlf-errors.{id}";

        CrlfPoisonHandler.Reset();
        await using var dlqSubscription = await NatsTestHelpers.SubscribeRawAsync(natsUrl, deadLetterSubject);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Leftovers";
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s => s.WithSubjects($"dlqcrlf.{id}.>"));

                opts.Policies.DisableConventionalLocalRouting();

                opts.ListenToNatsSubject(subject)
                    .UseJetStream(stream, $"dlqcrlf-consumer-{id}")
                    .ConfigureDeadLetterQueue(1, deadLetterSubject);

                opts.PublishMessage<CrlfPoisonMessage>().ToNatsSubject(subject).UseJetStream(stream);
                opts.Policies.OnException<CrlfPoisonException>().MoveToErrorQueue();
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await host.MessageBus().SendAsync(new CrlfPoisonMessage(id));
        await CrlfPoisonHandler.WaitForAttemptAsync();

        // On Windows the stack trace itself is CRLF-delimited; on every platform the exception message below
        // is, which lands in the exception-message header the same way. Before the fix NATS.Net refused the
        // header and the forward never happened
        var deadLettered = await dlqSubscription.ReadAsync(30.Seconds());
        deadLettered.ShouldNotBeNull("the dead letter copy was never forwarded");

        Encoding.UTF8.GetString(deadLettered.Value.Data!).ShouldContain(id);
        var message = deadLettered.Value.Headers![DeadLetterQueueConstants.ExceptionMessageHeader].ToString();
        message.ShouldContain("line one");
        message.ShouldContain("line two");
        message.ShouldNotContain("\r");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // ---- mirror / sources on a declared stream ------------------------------------------------

    [Fact]
    public async Task a_declared_mirror_stream_is_created_as_a_mirror_and_verifies_clean()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var origin = $"LEFTOVER_ORIGIN_{suffix}";
        var mirror = $"LEFTOVER_MIRROR_{suffix}";
        var subject = $"leftover.origin.{suffix}";

        Action<NatsTransportExpression> declare = nats => nats
            .DefineStream(origin, s => s.WithSubjects(subject))
            .DefineStream(mirror, s => s.MirrorOf(origin));

        using (var host = await startAsync(NatsProvisioning.CreateOnly, declare))
        {
            await using var connection = await connectAsync();
            var js = connection.CreateJetStreamContext();

            var config = (await js.GetStreamAsync(mirror, cancellationToken: TestContext.Current.CancellationToken)).Info.Config;
            config.Mirror.ShouldNotBeNull();
            config.Mirror!.Name.ShouldBe(origin);

            // ...and it really mirrors: a message published to the origin shows up in the mirror
            await js.PublishAsync(subject, "hello"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
            var mirrored = await waitForMessagesAsync(js, mirror, 1);
            mirrored.ShouldBe(1);

            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // Verify compares the mirror and passes for what Wolverine itself created
        using var verified = await startAsync(NatsProvisioning.Verify, declare);
        await verified.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_declared_source_aggregates_the_origin_and_verify_reports_a_different_origin()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var origin = $"LEFTOVER_SRC_{suffix}";
        var other = $"LEFTOVER_OTHER_{suffix}";
        var aggregate = $"LEFTOVER_AGG_{suffix}";
        var subject = $"leftover.src.{suffix}";

        using (var host = await startAsync(NatsProvisioning.CreateOnly, nats => nats
                   .DefineStream(origin, s => s.WithSubjects(subject))
                   .DefineStream(other, s => s.WithSubjects(subject + ".other"))
                   .DefineStream(aggregate, s => s.WithSubjects(subject + ".own").SourcedFrom(origin))))
        {
            await using var connection = await connectAsync();
            var js = connection.CreateJetStreamContext();

            var config = (await js.GetStreamAsync(aggregate, cancellationToken: TestContext.Current.CancellationToken)).Info.Config;
            config.Sources.ShouldNotBeNull();
            config.Sources!.Single().Name.ShouldBe(origin);

            await js.PublishAsync(subject, "hello"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
            (await waitForMessagesAsync(js, aggregate, 1)).ShouldBe(1);

            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // Declaring a different origin is drift, and Verify says so
        var exception = await Should.ThrowAsync<Exception>(() => startAsync(NatsProvisioning.Verify, nats => nats
            .DefineStream(origin, s => s.WithSubjects(subject))
            .DefineStream(other, s => s.WithSubjects(subject + ".other"))
            .DefineStream(aggregate, s => s.WithSubjects(subject + ".own").SourcedFrom(other))));

        var message = flatten(exception);
        _output.WriteLine(message);
        message.ShouldContain($"stream '{aggregate}'");
        message.ShouldContain("Sources: configured");
        message.ShouldContain(other);
    }

    // Placement needs a cluster to mean anything, so the single-node server cannot honour it; what can be
    // pinned is that it is declared, overlaid and compared like the other managed settings
    [Fact]
    public void placement_is_built_overlaid_and_compared()
    {
        var configuration = new StreamConfiguration().WithSubject("placed.>").PlacedOn("east", "ssd", "eu");
        var desired = JetStreamProvisioning.BuildStreamConfig("PLACED", configuration, new JetStreamDefaults());

        desired.Placement.ShouldNotBeNull();
        desired.Placement!.Cluster.ShouldBe("east");
        desired.Placement.Tags.ShouldBe(["ssd", "eu"]);

        var existing = new StreamConfig("PLACED", ["placed.>"]) { Placement = new Placement { Cluster = "west" } };
        JetStreamProvisioning.OverlayManagedSettings(existing, desired).Placement!.Cluster.ShouldBe("east");

        var differences = JetStreamProvisioning.CompareStream(desired,
            new StreamConfig("PLACED", ["placed.>"]) { Placement = new Placement { Cluster = "west" } });
        differences.ShouldContain(x => x.StartsWith("Placement: configured cluster east, tags [eu, ssd], server cluster west, no tags"));

        // Undeclared means unmanaged: the server's placement is neither overlaid nor reported
        var undeclared = JetStreamProvisioning.BuildStreamConfig("PLACED",
            new StreamConfiguration().WithSubject("placed.>"), new JetStreamDefaults());
        JetStreamProvisioning.OverlayManagedSettings(
            new StreamConfig("PLACED", ["placed.>"]) { Placement = new Placement { Cluster = "west" } }, undeclared)
            .Placement!.Cluster.ShouldBe("west");
        JetStreamProvisioning.CompareStream(undeclared,
                new StreamConfig("PLACED", ["placed.>"])
                {
                    Placement = new Placement { Cluster = "west" },
                    DuplicateWindow = new JetStreamDefaults().DuplicateWindow
                })
            .ShouldBeEmpty();
    }

    // ---- JetStream publish options extension point -------------------------------------------

    [Fact]
    public async Task configure_jetstream_publish_has_the_last_word_over_the_publish_options()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stream = $"LEFTOVER_PUB_{suffix}";
        var subject = $"leftover.pub.{suffix}";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Leftovers";
                opts.UseNats(_fixture.ConnectionString)
                    .AutoProvision()
                    .DefineStream(stream, s => s.WithSubjects(subject).WithDeduplicationWindow(2.Minutes()))
                    // Every publish carries the SAME Nats-Msg-Id, so the stream keeps only the first one: an
                    // observable proof that the options the callback returns are the ones the publish used
                    .ConfigureJetStreamPublish((_, pubOpts) => pubOpts with { MsgId = $"constant-{suffix}" });

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<LeftoverMessage>().ToNatsSubject(subject).UseJetStream(stream).SendInline();
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var bus = host.MessageBus();
        await bus.SendAsync(new LeftoverMessage("one"));
        await bus.SendAsync(new LeftoverMessage("two"));
        await bus.SendAsync(new LeftoverMessage("three"));

        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        var info = await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        info.Info.State.Messages.ShouldBe(1L, "the two later publishes should have been deduplicated by the constant Nats-Msg-Id");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static bool isDisposed(NatsConnection connection)
    {
        // An interlocked int in NATS.Net 2.8: 1 once DisposeAsync has run
        var field = typeof(NatsConnection).GetField("_isDisposed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.ShouldNotBeNull("NatsConnection._isDisposed was renamed; this probe needs updating");
        return field.GetValue(connection) is int flag && flag == 1;
    }

    private async Task<IHost> startAsync(NatsProvisioning provisioning, Action<NatsTransportExpression> configure)
    {
        return await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Leftovers";
                var nats = opts.UseNats(_fixture.ConnectionString).Provisioning(provisioning);
                nats.AutoProvision();
                configure(nats);
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<NatsConnection> connectAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        return connection;
    }

    private static async Task<long> waitForMessagesAsync(INatsJSContext js, string stream, long expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        long count = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            count = (await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info.State.Messages;
            if (count >= expected) return count;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return count;
    }

    private static string flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}

public record LeftoverMessage(string Name);

public record CrlfPoisonMessage(string Id);

public class CrlfPoisonException : Exception
{
    public CrlfPoisonException(string message) : base(message)
    {
    }
}

[WolverineHandler]
public static class CrlfPoisonHandler
{
    private static TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset() => _attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task WaitForAttemptAsync() => _attempted.Task.WaitAsync(30.Seconds());

    public static void Handle(CrlfPoisonMessage message)
    {
        _attempted.TrySetResult();
        throw new CrlfPoisonException("line one\r\nline two");
    }
}
