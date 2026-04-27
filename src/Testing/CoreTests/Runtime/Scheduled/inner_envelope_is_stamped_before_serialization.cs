using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.SharedMemory;
using Xunit;

namespace CoreTests.Runtime.Scheduled;

public class inner_envelope_is_stamped_before_serialization : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        ScheduledEnvelopeCapture.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.EnableRelayOfUserName = true;
                opts.PublishAllMessages().ToSharedMemoryTopic("scheduled_tenant_topic");
                opts.ListenToSharedMemorySubscription("scheduled_tenant_topic", "sub").ProcessInline();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task scheduled_send_to_non_native_transport_preserves_context_fields()
    {
        var bus = (MessageBus)_host.MessageBus();
        bus.TenantId = "red";
        bus.CorrelationId = "corr-123";
        bus.UserName = "alice";

        var tracked = await _host.TrackActivity()
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(_ =>
                bus.PublishAsync(new Message1(), new DeliveryOptions { ScheduleDelay = 1.Minutes() }).AsTask());
        
        await tracked.PlayScheduledMessagesAsync(2.Hours());
        await Task.Delay(2.Minutes());
        
        var captured = await ScheduledEnvelopeCapture.WaitAsync(5.Seconds());
        captured.TenantId.ShouldBe("red");
        captured.CorrelationId.ShouldBe("corr-123");
        captured.UserName.ShouldBe("alice");
    }
}

public class Message1CapturingHandler
{
    public void Handle(Message1 _, Envelope envelope)
    {
        ScheduledEnvelopeCapture.Capture(envelope);
    }
}

public static class ScheduledEnvelopeCapture
{
    public record Snapshot(string? TenantId, string? CorrelationId, string? UserName);

    private static TaskCompletionSource<Snapshot> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        _tcs = new TaskCompletionSource<Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Capture(Envelope envelope)
    {
        _tcs.TrySetResult(new Snapshot(envelope.TenantId, envelope.CorrelationId, envelope.UserName));
    }

    public static Task<Snapshot> WaitAsync(TimeSpan timeout) => _tcs.Task.WaitAsync(timeout);
}
