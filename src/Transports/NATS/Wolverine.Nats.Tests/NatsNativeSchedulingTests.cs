using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Native JetStream scheduling end to end: every <c>ScheduleAsync()</c> is its own server-side schedule, the
/// listener never sees the control message and gets the materialized message once, at its due time, in every
/// endpoint mode. NATS identifies a schedule by its subject (ADR-51), so the control messages of two schedules
/// for one endpoint must not share a subject, and a consumer that covers the schedule subject would handle the
/// control message itself -- early, and with the materialized copy a second time. Needs nats-server 2.14.7 or
/// later (nats-server#8571).
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsNativeSchedulingTests
{
    private static readonly TimeSpan Delay = 5.Seconds();

    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsNativeSchedulingTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task two_schedules_for_one_endpoint_both_arrive_after_the_receiver_was_down()
    {
        var names = new Names();

        using var sender = await startSenderAsync(names);
        if (!supportsNativeScheduling(sender, names)) return;

        // The receiver is up while the messages are scheduled, so its consumer exists, then goes away before
        // anything is due -- a crash or a deployment. Nothing it held in memory may matter.
        using (await startReceiverAsync(names, EndpointMode.BufferedInMemory))
        {
            var bus = sender.MessageBus();
            await bus.ScheduleAsync(new NativeScheduledPing(names.RunId, "A"), Delay);
            await bus.ScheduleAsync(new NativeScheduledPing(names.RunId, "B"), Delay + 1.Seconds());
            await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        }

        await Task.Delay(Delay + 3.Seconds(), TestContext.Current.CancellationToken);

        using var restarted = await startReceiverAsync(names, EndpointMode.BufferedInMemory);
        await waitForAsync(names.RunId, 2, 15.Seconds());
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        NativeScheduledPingHandler.NamesFor(names.RunId).Order().ShouldBe(["A", "B"]);
    }

    [Theory]
    [InlineData(EndpointMode.BufferedInMemory)]
    [InlineData(EndpointMode.Inline)]
    [InlineData(EndpointMode.NativeAck)]
    public async Task an_online_listener_handles_a_scheduled_message_once_at_its_due_time(EndpointMode mode)
    {
        var names = new Names();

        using var sender = await startSenderAsync(names);
        if (!supportsNativeScheduling(sender, names)) return;

        using var receiver = await startReceiverAsync(names, mode);

        var scheduledAt = DateTimeOffset.UtcNow;
        await sender.MessageBus().ScheduleAsync(new NativeScheduledPing(names.RunId, "A"), Delay);

        await waitForAsync(names.RunId, 1, Delay + 15.Seconds());

        // Long enough for the second handling a listener consuming the control message used to produce at the
        // due time
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        var handled = NativeScheduledPingHandler.For(names.RunId);
        handled.Length.ShouldBe(1);
        assertNotEarly(handled[0], scheduledAt);
    }

    [Fact]
    public async Task a_wildcard_jetstream_listener_skips_the_control_message()
    {
        var names = new Names();

        using var sender = await startSenderAsync(names);
        if (!supportsNativeScheduling(sender, names)) return;

        // Its consumer covers {target}.scheduled.{id} as well
        using var receiver = await startReceiverAsync(names, EndpointMode.BufferedInMemory, $"{names.Root}.>");

        var scheduledAt = DateTimeOffset.UtcNow;
        await sender.MessageBus().ScheduleAsync(new NativeScheduledPing(names.RunId, "A"), Delay);

        await waitForAsync(names.RunId, 1, Delay + 15.Seconds());
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        var handled = NativeScheduledPingHandler.For(names.RunId);
        handled.Length.ShouldBe(1);
        assertNotEarly(handled[0], scheduledAt);
    }

    [Fact]
    public async Task a_wildcard_core_listener_skips_the_control_message()
    {
        var names = new Names();

        using var sender = await startSenderAsync(names);
        if (!supportsNativeScheduling(sender, names)) return;

        // A JetStream publish is also a core NATS message on its subject, so a core subscription on a wildcard
        // sees the control message. The materialized message is only stored in the stream, never published to
        // core subscriptions, so a core listener has nothing to handle here at all.
        using var receiver = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NativeScheduleCoreReceiver";
                opts.UseNats(_fixture.ConnectionString);
                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToNatsSubject($"{names.Root}.>").ProcessInline();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await sender.MessageBus().ScheduleAsync(new NativeScheduledPing(names.RunId, "A"), Delay);

        await Task.Delay(Delay + 3.Seconds(), TestContext.Current.CancellationToken);

        NativeScheduledPingHandler.For(names.RunId).ShouldBeEmpty();
    }

    [Fact]
    public async Task each_schedule_gets_its_own_schedule_subject()
    {
        var names = new Names();

        using var sender = await startSenderAsync(names);
        if (!supportsNativeScheduling(sender, names)) return;

        var bus = sender.MessageBus();
        await bus.ScheduleAsync(new NativeScheduledPing(names.RunId, "A"), 1.Hours());
        await bus.ScheduleAsync(new NativeScheduledPing(names.RunId, "B"), 1.Hours());

        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        var stream = await connection.CreateJetStreamContext()
            .GetStreamAsync(names.Stream, new StreamInfoRequest { SubjectsFilter = ">" },
                TestContext.Current.CancellationToken);

        // Two pending schedules, neither replaced by the other
        stream.Info.State.Messages.ShouldBe(2);
        var subjects = stream.Info.State.Subjects!.Keys.ToArray();
        subjects.Length.ShouldBe(2);
        subjects.ShouldAllBe(x => x.StartsWith(names.Target + ".scheduled."));
    }

    [Fact]
    public async Task an_interest_stream_falls_back_to_wolverine_scheduling()
    {
        var names = new Names();

        // Acking the control message deletes it from an interest stream, so the schedule would never fire
        using var sender = await startSenderAsync(names, interest: true);
        var transport = sender.Services.GetRequiredService<IWolverineRuntime>().Options.Transports
            .GetOrCreate<NatsTransport>();
        if (!transport.ServerSupportsScheduledSend) return;

        sender.Services.GetRequiredService<IWolverineRuntime>().Endpoints
            .GetOrBuildSendingAgent(new Uri($"nats://subject/{names.Target}"))
            .SupportsNativeScheduledSend.ShouldBeFalse();

        using var receiver = await startReceiverAsync(names, EndpointMode.BufferedInMemory, interest: true);

        await sender.MessageBus().ScheduleAsync(new NativeScheduledPing(names.RunId, "A"), 2.Seconds());
        await waitForAsync(names.RunId, 1, 15.Seconds());
    }

    [Fact]
    public async Task a_stream_without_the_schedule_subjects_falls_back_to_wolverine_scheduling()
    {
        var names = new Names();

        // Declared with the exact subjects the single schedule subject used to need. Each schedule now has a subject
        // of its own under it, which this stream does not capture, so every native schedule would go nowhere
        void declareExactSubjects(WolverineOptions opts) => opts.UseNats(_fixture.ConnectionString)
            .DefineStream(names.Stream, s => s.WithSubjects(names.Target, $"{names.Target}.scheduled")
                .EnableScheduledDelivery());

        using var sender = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NativeScheduleSender";
                declareExactSubjects(opts);
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<NativeScheduledPing>().ToNatsSubject(names.Target).UseJetStream(names.Stream);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = sender.Services.GetRequiredService<IWolverineRuntime>();
        if (!runtime.Options.Transports.GetOrCreate<NatsTransport>().ServerSupportsScheduledSend) return;

        runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"nats://subject/{names.Target}"))
            .SupportsNativeScheduledSend.ShouldBeFalse();

        using var receiver = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NativeScheduleReceiver";
                declareExactSubjects(opts);
                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToNatsSubject(names.Target).UseJetStream(names.Stream, names.Consumer);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await sender.MessageBus().ScheduleAsync(new NativeScheduledPing(names.RunId, "A"), 2.Seconds());
        await waitForAsync(names.RunId, 1, 15.Seconds());
    }

    private void assertNotEarly(HandledPing handled, DateTimeOffset scheduledAt)
    {
        // The broker's clock decides the due time, so allow for a little skew between it and this machine; a
        // control message handled on arrival shows up within a second of scheduling
        var after = handled.At - scheduledAt;
        _output.WriteLine($"{handled.Name} handled {after.TotalMilliseconds:F0} ms after scheduling");
        after.ShouldBeGreaterThan(Delay - 2.Seconds());
    }

    private bool supportsNativeScheduling(IHost sender, Names names)
    {
        var runtime = sender.Services.GetRequiredService<IWolverineRuntime>();
        if (!runtime.Options.Transports.GetOrCreate<NatsTransport>().ServerSupportsScheduledSend)
        {
            _output.WriteLine("The NATS server does not support message schedules, skipping");
            return false;
        }

        // Otherwise this would test Wolverine's own scheduling
        runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"nats://subject/{names.Target}"))
            .SupportsNativeScheduledSend.ShouldBeTrue();

        return true;
    }

    private Task<IHost> startSenderAsync(Names names, bool interest = false)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NativeScheduleSender";
                declare(opts, names, interest);
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<NativeScheduledPing>().ToNatsSubject(names.Target).UseJetStream(names.Stream);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private Task<IHost> startReceiverAsync(Names names, EndpointMode mode, string? subject = null,
        bool interest = false)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NativeScheduleReceiver";
                declare(opts, names, interest);
                opts.Policies.DisableConventionalLocalRouting();

                var listener = opts.ListenToNatsSubject(subject ?? names.Target)
                    .UseJetStream(names.Stream, names.Consumer);

                switch (mode)
                {
                    case EndpointMode.Inline:
                        listener.ProcessInline();
                        break;
                    case EndpointMode.NativeAck:
                        listener.ProcessInParallelWithNativeAcks();
                        break;
                    default:
                        listener.BufferedInMemory();
                        break;
                }
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private void declare(WolverineOptions opts, Names names, bool interest)
    {
        var nats = opts.UseNats(_fixture.ConnectionString);
        if (interest)
        {
            nats.DefineWorkQueueStream(names.Stream, s => s.EnableScheduledDelivery(), $"{names.Root}.>");
        }
        else
        {
            nats.DefineStream(names.Stream, s => s.WithSubjects($"{names.Root}.>").EnableScheduledDelivery());
        }
    }

    private static async Task waitForAsync(string runId, int count, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (NativeScheduledPingHandler.For(runId).Length < count)
        {
            try
            {
                await Task.Delay(100.Milliseconds(), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private sealed class Names
    {
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public string Stream => $"NATIVESCHED_{RunId}";
        public string Root => $"nativesched.{RunId}";
        public string Target => $"{Root}.target";
        public string Consumer => $"nativesched-{RunId}";
    }
}

public record NativeScheduledPing(string RunId, string Name);

public record HandledPing(string Name, DateTimeOffset At);

public static class NativeScheduledPingHandler
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<HandledPing>> _handled = new();

    public static void Handle(NativeScheduledPing message)
    {
        _handled.GetOrAdd(message.RunId, _ => new ConcurrentQueue<HandledPing>())
            .Enqueue(new HandledPing(message.Name, DateTimeOffset.UtcNow));
    }

    public static HandledPing[] For(string runId) =>
        _handled.TryGetValue(runId, out var handled) ? handled.ToArray() : [];

    public static string[] NamesFor(string runId) => For(runId).Select(x => x.Name).ToArray();
}
