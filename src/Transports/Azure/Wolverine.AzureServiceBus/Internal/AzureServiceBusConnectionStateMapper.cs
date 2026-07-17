using Azure.Messaging.ServiceBus;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Internal;

/// <summary>
/// GH-3237: maps a real Azure Service Bus SDK failure to a degraded <see cref="TransportConnectionState"/>.
/// The ASB SDK exposes no queryable connection state, so the only honest signals are error callbacks — this
/// mapper may only ever move state toward trouble (Reconnecting/Disconnected), never synthesize Connected.
/// Message-level failures (lock lost, size exceeded, ...) say nothing about the connection and map to null.
/// </summary>
internal static class AzureServiceBusConnectionStateMapper
{
    /// <summary>
    /// Returns the degraded connection state implied by the given receive-side failure, or null when the
    /// failure carries no connection-level information and the current state should be left alone.
    /// </summary>
    internal static TransportConnectionState? StateForError(Exception? exception)
    {
        switch (exception)
        {
            // Auth revoked/expired — the listener cannot consume until credentials are fixed
            case UnauthorizedAccessException:
                return TransportConnectionState.Disconnected;

            case ServiceBusException sbe:
                switch (sbe.Reason)
                {
                    // Transient comms trouble; the SDK is retrying the AMQP link under the covers
                    case ServiceBusFailureReason.ServiceCommunicationProblem:
                    case ServiceBusFailureReason.ServiceTimeout:
                    case ServiceBusFailureReason.ServiceBusy:
                        return TransportConnectionState.Reconnecting;

                    // The entity is gone or disabled — retrying will not bring consumption back
                    case ServiceBusFailureReason.MessagingEntityNotFound:
                    case ServiceBusFailureReason.MessagingEntityDisabled:
                        return TransportConnectionState.Disconnected;

                    default:
                        return null;
                }

            default:
                return null;
        }
    }
}
