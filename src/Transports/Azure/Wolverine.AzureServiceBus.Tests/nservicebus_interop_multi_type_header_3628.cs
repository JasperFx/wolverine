using Azure.Messaging.ServiceBus;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
///     GH-3628. A Wolverine listener with <c>UseNServiceBusInterop()</c> never received an event whose type
///     implemented any interface the publisher's NServiceBus conventions also class as a message type: the
///     <c>NServiceBus.EnclosedMessageTypes</c> header is a ';'-delimited list in that case, the mapper handed the
///     whole thing to <see cref="Type.GetType(string)" />, and mapping threw
///     <c>FileLoadException: The given assembly name was invalid.</c>
///
///     <para>These run against the real mapper each of the three ASB endpoint types builds, with no broker
///     involved, because the bug lived in the mapping and not in the transport.</para>
/// </summary>
public class nservicebus_interop_multi_type_header_3628
{
    // MyEvent : IMyEvent as NServiceBus serializes it — concrete type first, then the qualifying interface.
    // Neither assembly exists here, which is exactly the position a Wolverine subscriber is in when the
    // publisher's contracts are not deployed on its side.
    private const string MultiTypeHeader =
        "MyApp.Messages.MyEvent, MyApp.Messages, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null;" +
        "MyApp.Abstractions.IMyEvent, MyApp.Abstractions, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    public static IEnumerable<object[]> Endpoints()
    {
        yield return ["queue", new Uri("asb://queue/orders")];
        yield return ["topic", new Uri("asb://topic/events")];
        yield return ["subscription", new Uri("asb://topic/events/audit")];
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void a_multi_type_header_maps_without_throwing(string _, Uri uri)
    {
        mapIncoming(uri, MultiTypeHeader)
            .MessageType.ShouldBe("MyApp.Messages.MyEvent");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void a_single_type_header_still_maps_as_before(string _, Uri uri)
    {
        // The one-entry shape is what worked all along, and must keep working: a type that IS loadable here
        // resolves through to Wolverine's message type name rather than the raw header text.
        var header = $"{typeof(NsbInteropOrder).FullName}, {typeof(NsbInteropOrder).Assembly.GetName().Name}";

        mapIncoming(uri, header)
            .MessageType.ShouldBe(typeof(NsbInteropOrder).ToMessageTypeName());
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void a_malformed_header_does_not_take_the_listener_down(string _, Uri uri)
    {
        // Previously any header Type.GetType could not parse threw out of the mapping and killed the receive
        // loop. Now it maps to an unroutable message type, which Wolverine dead-letters.
        Should.NotThrow(() => mapIncoming(uri, "!!! definitely not a type name"));
    }

    private static Envelope mapIncoming(Uri uri, string enclosedMessageTypes)
    {
        var transport = new AzureServiceBusTransport();
        var endpoint = (AzureServiceBusEndpoint)transport.GetOrCreateEndpoint(uri);

        switch (endpoint)
        {
            case AzureServiceBusQueue queue:
                queue.UseNServiceBusInterop();
                break;
            case AzureServiceBusTopic topic:
                topic.UseNServiceBusInterop();
                break;
            case AzureServiceBusSubscription subscription:
                subscription.UseNServiceBusInterop();
                break;
            default:
                throw new InvalidOperationException($"Unexpected endpoint type {endpoint.GetType().Name}");
        }

        var mapper = endpoint.BuildMapper(Substitute.For<IWolverineRuntime>());

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("{}"),
            messageId: Guid.NewGuid().ToString(),
            properties: new Dictionary<string, object>
            {
                ["NServiceBus.EnclosedMessageTypes"] = enclosedMessageTypes
            });

        var envelope = new Envelope();
        mapper.MapIncomingToEnvelope(envelope, message);

        return envelope;
    }

    public record NsbInteropOrder(string Id);
}
