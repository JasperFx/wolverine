using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

// GH-3237: the Azure Service Bus SDK exposes no queryable connection state, so the listeners derive a
// degrade-only state from real error callbacks. The resting state for a healthy listener is Unknown —
// a heuristic may move the state toward trouble, but must never synthesize Connected.
public class connection_state_3237
{
    [Theory]
    [InlineData(ServiceBusFailureReason.ServiceCommunicationProblem, TransportConnectionState.Reconnecting)]
    [InlineData(ServiceBusFailureReason.ServiceTimeout, TransportConnectionState.Reconnecting)]
    [InlineData(ServiceBusFailureReason.ServiceBusy, TransportConnectionState.Reconnecting)]
    [InlineData(ServiceBusFailureReason.MessagingEntityNotFound, TransportConnectionState.Disconnected)]
    [InlineData(ServiceBusFailureReason.MessagingEntityDisabled, TransportConnectionState.Disconnected)]
    public void maps_connection_level_service_bus_failures(ServiceBusFailureReason reason,
        TransportConnectionState expected)
    {
        AzureServiceBusConnectionStateMapper.StateForError(new ServiceBusException("boom", reason))
            .ShouldBe(expected);
    }

    [Theory]
    [InlineData(ServiceBusFailureReason.MessageLockLost)]
    [InlineData(ServiceBusFailureReason.SessionLockLost)]
    [InlineData(ServiceBusFailureReason.MessageSizeExceeded)]
    [InlineData(ServiceBusFailureReason.QuotaExceeded)]
    [InlineData(ServiceBusFailureReason.GeneralError)]
    public void message_level_failures_say_nothing_about_the_connection(ServiceBusFailureReason reason)
    {
        AzureServiceBusConnectionStateMapper.StateForError(new ServiceBusException("boom", reason))
            .ShouldBeNull();
    }

    [Fact]
    public void unauthorized_access_means_disconnected()
    {
        AzureServiceBusConnectionStateMapper.StateForError(new UnauthorizedAccessException("expired"))
            .ShouldBe(TransportConnectionState.Disconnected);
    }

    [Fact]
    public void unrecognized_exceptions_leave_the_state_alone()
    {
        AzureServiceBusConnectionStateMapper.StateForError(new InvalidOperationException("unrelated"))
            .ShouldBeNull();
        AzureServiceBusConnectionStateMapper.StateForError(null).ShouldBeNull();
    }

    [Fact]
    public async Task healthy_listeners_rest_at_unknown_and_never_report_connected()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting().AutoProvision().AutoPurgeOnStartup();

                // One batched (default) and one inline listener so both listener types are covered
                opts.ListenToAzureServiceBusQueue("connstate-batched");
                opts.PublishMessage<ConnStateMessage>().ToAzureServiceBusQueue("connstate-batched");

                opts.ListenToAzureServiceBusQueue("connstate-inline").ProcessInline();
            }).StartAsync();

        // Prove the pipe actually works...
        await host.TrackActivity().IncludeExternalTransports().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new ConnStateMessage());

        // ...and that a working listener still reports Unknown, never a synthesized Connected
        var snapshots = host.GetRuntime().Endpoints.CollectEndpointHealth()
            .Where(s => s.Direction == EndpointDirection.Listening && s.Uri.Scheme == "asb")
            .ToArray();

        snapshots.ShouldNotBeEmpty();
        foreach (var snapshot in snapshots)
        {
            snapshot.ConnectionState.ShouldBe(TransportConnectionState.Unknown);
        }
    }
}

public record ConnStateMessage;

public static class ConnStateMessageHandler
{
    public static void Handle(ConnStateMessage message)
    {
    }
}
