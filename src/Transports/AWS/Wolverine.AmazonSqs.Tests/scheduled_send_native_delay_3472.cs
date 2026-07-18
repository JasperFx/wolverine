using System.Diagnostics;
using Amazon.SQS;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Tests;

public class NativeDelayDecisionTests
{
    private readonly AmazonSqsQueue theStandardQueue = new AmazonSqsTransport().Queues["standard"];
    private readonly AmazonSqsQueue theFifoQueue = new AmazonSqsTransport().Queues["ordered.fifo"];
    private readonly DateTimeOffset theCurrentTime = DateTimeOffset.UtcNow;

    [Fact]
    public void standard_queue_can_schedule_natively_with_no_scheduled_time()
    {
        theStandardQueue.CanScheduleNatively(new Envelope(), theCurrentTime).ShouldBeTrue();
    }

    [Fact]
    public void standard_queue_can_schedule_natively_within_15_minutes()
    {
        var envelope = new Envelope { ScheduledTime = theCurrentTime.AddMinutes(5) };
        theStandardQueue.CanScheduleNatively(envelope, theCurrentTime).ShouldBeTrue();

        envelope.ScheduledTime = theCurrentTime.AddSeconds(AmazonSqsQueue.MaximumSqsDelaySeconds);
        theStandardQueue.CanScheduleNatively(envelope, theCurrentTime).ShouldBeTrue();
    }

    [Fact]
    public void standard_queue_cannot_schedule_natively_beyond_15_minutes()
    {
        var envelope = new Envelope { ScheduledTime = theCurrentTime.AddMinutes(16) };
        theStandardQueue.CanScheduleNatively(envelope, theCurrentTime).ShouldBeFalse();
    }

    [Fact]
    public void fifo_queue_can_never_schedule_natively()
    {
        theFifoQueue.CanScheduleNatively(new Envelope(), theCurrentTime).ShouldBeFalse();

        var envelope = new Envelope { ScheduledTime = theCurrentTime.AddSeconds(30) };
        theFifoQueue.CanScheduleNatively(envelope, theCurrentTime).ShouldBeFalse();
    }

    [Fact]
    public void delay_seconds_is_zero_with_no_scheduled_time()
    {
        theStandardQueue.NativeDelaySecondsFor(new Envelope(), theCurrentTime, NullLogger.Instance)
            .ShouldBe(0);
    }

    [Fact]
    public void delay_seconds_is_zero_for_past_scheduled_time()
    {
        var envelope = new Envelope { ScheduledTime = theCurrentTime.AddMinutes(-1) };
        theStandardQueue.NativeDelaySecondsFor(envelope, theCurrentTime, NullLogger.Instance)
            .ShouldBe(0);
    }

    [Fact]
    public void delay_seconds_rounds_up_to_whole_seconds()
    {
        var envelope = new Envelope { ScheduledTime = theCurrentTime.AddMilliseconds(4500) };
        theStandardQueue.NativeDelaySecondsFor(envelope, theCurrentTime, NullLogger.Instance)
            .ShouldBe(5);
    }

    [Fact]
    public void delay_seconds_is_zero_for_fifo_queues()
    {
        var envelope = new Envelope { ScheduledTime = theCurrentTime.AddSeconds(30) };
        theFifoQueue.NativeDelaySecondsFor(envelope, theCurrentTime, NullLogger.Instance)
            .ShouldBe(0);
    }

    [Fact]
    public void delay_seconds_is_defensively_capped_at_the_sqs_maximum()
    {
        // The routing layer falls back to Wolverine scheduling before an envelope like this
        // can ever reach the sender, so this cap is defense in depth only
        var envelope = new Envelope { ScheduledTime = theCurrentTime.AddMinutes(30) };
        theStandardQueue.NativeDelaySecondsFor(envelope, theCurrentTime, NullLogger.Instance)
            .ShouldBe(AmazonSqsQueue.MaximumSqsDelaySeconds);
    }
}

public class scheduled_send_with_native_delay : IAsyncLifetime
{
    private const string QueueName = "native-delay-3472";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        // Deliberately storageless (no message store) so a natively delayed delivery
        // cannot be confused with Wolverine's own scheduled message polling
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToSqsQueue(QueueName);

                opts.PublishMessage<SqsNativeDelayMessage>().ToSqsQueue(QueueName);
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void sending_agent_makes_the_native_decision_per_envelope()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var agent = runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"sqs://{QueueName}"));

        agent.SupportsNativeScheduledSend.ShouldBeTrue();

        var utcNow = DateTimeOffset.UtcNow;

        var withinWindow = new Envelope { ScheduledTime = utcNow.AddMinutes(5) };
        agent.SupportsNativeScheduledSendFor(withinWindow, utcNow).ShouldBeTrue();

        var pastWindow = new Envelope { ScheduledTime = utcNow.AddMinutes(20) };
        agent.SupportsNativeScheduledSendFor(pastWindow, utcNow).ShouldBeFalse();
    }

    [Fact]
    public async Task scheduled_message_within_the_window_is_delivered_natively_after_the_delay()
    {
        var message = new SqsNativeDelayMessage("delayed");
        var stopwatch = Stopwatch.StartNew();

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(2.Minutes())
            .WaitForMessageToBeReceivedAt<SqsNativeDelayMessage>(_host)
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(message, 5.Seconds()).AsTask());

        stopwatch.Stop();

        session.Received.SingleMessage<SqsNativeDelayMessage>()
            .Name.ShouldBe(message.Name);

        // The broker honored the delay
        stopwatch.Elapsed.ShouldBeGreaterThan(4.Seconds());

        // Proves the native path: the envelope went straight out to SQS instead of being
        // wrapped for Wolverine's own scheduled message storage at local://durable
        session.Sent.RecordsInOrder()
            .Any(x => x.Envelope!.Destination?.Scheme == "sqs")
            .ShouldBeTrue();
        session.AllRecordsInOrder()
            .Any(x => x.Envelope?.Destination == Transports.TransportConstants.DurableLocalUri)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task scheduled_message_beyond_the_window_falls_back_to_wolverine_scheduling()
    {
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(new SqsNativeDelayMessage("way later"), 20.Minutes()).AsTask());

        // The message was captured by Wolverine's own scheduling (in memory here since
        // there is no message store) instead of being sent to the broker
        session.Scheduled.Envelopes()
            .Any(x => x.Message is SqsNativeDelayMessage)
            .ShouldBeTrue();

        session.Sent.RecordsInOrder()
            .Any(x => x.Envelope!.Destination?.Scheme == "sqs")
            .ShouldBeFalse();

        session.Received.RecordsInOrder().ShouldBeEmpty();
    }
}

public class inline_scheduled_send_with_native_delay : IAsyncLifetime
{
    private const string QueueName = "native-delay-inline-3472";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToSqsQueue(QueueName).ProcessInline();

                opts.PublishMessage<SqsInlineNativeDelayMessage>().ToSqsQueue(QueueName).SendInline();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task scheduled_message_is_delivered_natively_after_the_delay()
    {
        var message = new SqsInlineNativeDelayMessage("inline delayed");
        var stopwatch = Stopwatch.StartNew();

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(2.Minutes())
            .WaitForMessageToBeReceivedAt<SqsInlineNativeDelayMessage>(_host)
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(message, 5.Seconds()).AsTask());

        stopwatch.Stop();

        session.Received.SingleMessage<SqsInlineNativeDelayMessage>()
            .Name.ShouldBe(message.Name);

        stopwatch.Elapsed.ShouldBeGreaterThan(4.Seconds());

        session.Sent.RecordsInOrder()
            .Any(x => x.Envelope!.Destination?.Scheme == "sqs")
            .ShouldBeTrue();
        session.AllRecordsInOrder()
            .Any(x => x.Envelope?.Destination == Transports.TransportConstants.DurableLocalUri)
            .ShouldBeFalse();
    }
}

public class scheduled_send_to_fifo_queue_falls_back : IAsyncLifetime
{
    private const string QueueName = "native-delay-3472.fifo";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToSqsQueue(QueueName)
                    .ConfigureQueueCreation(request =>
                    {
                        request.Attributes ??= new Dictionary<string, string>();
                        request.Attributes[QueueAttributeName.FifoQueue] = "true";
                        request.Attributes[QueueAttributeName.ContentBasedDeduplication] = "true";
                    });

                opts.PublishMessage<SqsFifoDelayMessage>().ToSqsQueue(QueueName)
                    .ConfigureQueueCreation(request =>
                    {
                        request.Attributes ??= new Dictionary<string, string>();
                        request.Attributes[QueueAttributeName.FifoQueue] = "true";
                        request.Attributes[QueueAttributeName.ContentBasedDeduplication] = "true";
                    });
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void fifo_sending_agent_reports_no_native_scheduled_send_support()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var agent = runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"sqs://{QueueName}"));

        agent.SupportsNativeScheduledSend.ShouldBeFalse();

        var utcNow = DateTimeOffset.UtcNow;
        var withinWindow = new Envelope { ScheduledTime = utcNow.AddSeconds(30) };
        agent.SupportsNativeScheduledSendFor(withinWindow, utcNow).ShouldBeFalse();
    }

    [Fact]
    public async Task scheduled_message_to_fifo_queue_is_delivered_via_the_fallback()
    {
        var message = new SqsFifoDelayMessage("fifo delayed");

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(2.Minutes())
            .WaitForMessageToBeReceivedAt<SqsFifoDelayMessage>(_host)
            .ExecuteAndWaitAsync(c => c.PublishAsync(message, new DeliveryOptions
            {
                ScheduleDelay = 5.Seconds(),
                GroupId = "native-delay-tests"
            }).AsTask());

        session.Received.SingleMessage<SqsFifoDelayMessage>()
            .Name.ShouldBe(message.Name);

        // The fallback captured the message with Wolverine's own scheduling first
        session.Scheduled.Envelopes()
            .Any(x => x.Message is SqsFifoDelayMessage)
            .ShouldBeTrue();
    }
}

public record SqsNativeDelayMessage(string Name);

public record SqsInlineNativeDelayMessage(string Name);

public record SqsFifoDelayMessage(string Name);

public static class NativeDelayMessageHandler
{
    public static void Handle(SqsNativeDelayMessage message)
    {
        // nothing
    }

    public static void Handle(SqsInlineNativeDelayMessage message)
    {
        // nothing
    }

    public static void Handle(SqsFifoDelayMessage message)
    {
        // nothing
    }
}
