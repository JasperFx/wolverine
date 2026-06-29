using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Scheduled;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using CoreTests.Configuration;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/3263.
///
/// <para>
/// When a message is *scheduled* for delayed delivery to a transport that does not
/// support native scheduled send (e.g. Kafka), Wolverine wraps the outgoing envelope
/// in a durable "scheduled-envelope" (<see cref="EnvelopeScheduleExtensions.ForScheduledSend"/>),
/// persists it, and later recovers and forwards it when the schedule fires
/// (<c>ScheduledSendEnvelopeHandler</c> → <c>MessageContext.ForwardScheduledEnvelopeAsync</c>).
/// </para>
///
/// <para>
/// The configured sender <see cref="IWireTap"/> is an in-memory-only reference on the
/// envelope; it does not survive the durable serialization round trip. The forwarding
/// code rebuilds the sender and serializer for the recovered envelope but never
/// re-attaches the destination endpoint's wire tap, so <c>RecordSuccessAsync</c> is
/// never called for scheduled sends — the audit trail silently drops them.
/// </para>
/// </summary>
public class Bug_3263_wire_tap_on_scheduled_send : IAsyncLifetime
{
    private readonly RecordingWireTap _wireTap = new();
    private IHost _host = null!;

    private static readonly Uri TheDestination = "stub://wire-tap-outbound".ToUri();

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddSingleton<IWireTap>(_wireTap);

                opts.PublishMessage<ScheduledAuditMessage>()
                    .To(TheDestination)
                    .UseWireTap();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task wire_tap_fires_for_scheduled_send_after_durable_recovery()
    {
        var runtime = _host.GetRuntime();

        var endpoint = runtime.Endpoints.EndpointFor(TheDestination);
        endpoint.ShouldNotBeNull();
        endpoint.WireTap.ShouldNotBeNull("Precondition: the sender wire tap should be resolved on the endpoint");

        var sender = runtime.Endpoints.GetOrBuildSendingAgent(TheDestination);

        // Build the outgoing envelope exactly as the routing layer would, including
        // the resolved sender wire tap. This is the in-memory envelope that carries
        // the WireTap correctly.
        var original = new Envelope(new ScheduledAuditMessage("audit me"))
        {
            Destination = TheDestination,
            Sender = sender,
            Serializer = runtime.Options.DefaultSerializer,
            ContentType = runtime.Options.DefaultSerializer!.ContentType,
            WireTap = endpoint.WireTap,
            ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        // The scheduled send to a non-native-scheduling transport is wrapped into a
        // durable "scheduled-envelope" that gets persisted.
        var wrapper = original.ForScheduledSend(sender);

        // Simulate durable recovery: the wrapper is reconstituted from storage and its
        // inner payload is deserialized into a fresh Envelope. The in-memory-only
        // WireTap reference does NOT survive this round trip.
        var recovered = (Envelope)EnvelopeReaderWriter.Instance.ReadFromData(wrapper.Data!);
        recovered.WireTap.ShouldBeNull("After durable recovery the in-memory wire tap reference is gone");
        recovered.Destination.ShouldBe(TheDestination);

        // This is exactly what ScheduledSendEnvelopeHandler does when the schedule fires.
        recovered.Status = EnvelopeStatus.Outgoing;
        var context = new MessageContext(runtime);
        await context.ForwardScheduledEnvelopeAsync(recovered);

        // The send-side wire tap is fired fire-and-forget from WolverineRuntime.Sent.
        await Task.Delay(250);

        _wireTap.Successes.ShouldContain(
            e => e.MessageType == typeof(ScheduledAuditMessage).ToMessageTypeName(),
            "The sender wire tap should record the scheduled message when it is finally sent");
    }

    [Fact]
    public async Task wire_tap_fires_for_recovered_outgoing_message()
    {
        var runtime = _host.GetRuntime();

        var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(TheDestination);
        sendingAgent.Endpoint.WireTap.ShouldNotBeNull(
            "Precondition: the sender wire tap should be resolved on the endpoint");

        // A persisted outgoing envelope recovered from storage has no in-memory wire tap
        // reference — it did not survive serialization.
        var recovered = new Envelope(new ScheduledAuditMessage("recover me"))
        {
            Destination = TheDestination,
            Serializer = runtime.Options.DefaultSerializer,
            ContentType = runtime.Options.DefaultSerializer!.ContentType
        };
        recovered.WireTap.ShouldBeNull();

        var store = Substitute.For<IMessageStore>();
        var outbox = Substitute.For<IMessageOutbox>();
        store.Outbox.Returns(outbox);
        outbox.LoadOutgoingAsync(TheDestination)
            .Returns(new List<Envelope> { recovered });
        outbox.DiscardAndReassignOutgoingAsync(Arg.Any<Envelope[]>(), Arg.Any<Envelope[]>(), Arg.Any<int>())
            .Returns(Task.CompletedTask);

        var command = new RecoverOutgoingMessagesCommand(sendingAgent, store, NullLogger.Instance);
        await command.ExecuteAsync(runtime, CancellationToken.None);

        // The send-side wire tap is fired fire-and-forget from WolverineRuntime.Sent.
        await Task.Delay(250);

        _wireTap.Successes.ShouldContain(
            e => e.MessageType == typeof(ScheduledAuditMessage).ToMessageTypeName(),
            "The sender wire tap should record a recovered outgoing message when it is finally sent");
    }
}

public record ScheduledAuditMessage(string Name);
