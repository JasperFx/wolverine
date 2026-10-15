using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Transport.Push;

/// <summary>
///     GH-5016. Turns the HTTP request a broker pushes (a Pub/Sub push subscription, SNS or EventBridge to HTTP, an
///     Event Grid subscription and so on) into Wolverine <see cref="Envelope" />s for
///     <see cref="PushReceiverEndpointExtensions.MapWolverinePushReceiver(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, IPushEnvelopeMapper, System.Action{PushReceiverOptions})" />.
///     Each transport supplies its own, usually by wrapping the envelope mapper it already uses for pulled messages
/// </summary>
public interface IPushEnvelopeMapper
{
    /// <summary>
    ///     Read the pushed request into envelopes, usually exactly one. A request may carry several when the sender
    ///     batched them into one broker message, as Wolverine's own batching senders do. For each envelope set either
    ///     <see cref="Envelope.Message" />, or <see cref="Envelope.Data" /> with <see cref="Envelope.MessageType" /> and
    ///     <see cref="Envelope.ContentType" /> for Wolverine to deserialize. Set <see cref="Envelope.Id" /> from the
    ///     broker's message id when it has one, so the optional inbox deduplication recognizes a redelivery, and set
    ///     <see cref="Envelope.Attempts" /> to the broker's delivery count minus one when it has one, so error policies
    ///     that count attempts escalate across redeliveries (GH-5048). Return an
    ///     empty list when the request does not carry a message, and the receiver answers 400 without handling anything
    /// </summary>
    ValueTask<IReadOnlyList<Envelope>> MapAsync(HttpContext context, CancellationToken cancellation);
}
