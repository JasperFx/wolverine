using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Transport.Push;

/// <summary>
///     GH-5016. Turns the HTTP request a broker pushes (a Pub/Sub push subscription, SNS or EventBridge to HTTP, an
///     Event Grid subscription and so on) into a Wolverine <see cref="Envelope" /> for
///     <see cref="PushReceiverEndpointExtensions.MapWolverinePushReceiver(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, IPushEnvelopeMapper, System.Action{PushReceiverOptions})" />.
///     Each transport supplies its own, usually by wrapping the envelope mapper it already uses for pulled messages
/// </summary>
public interface IPushEnvelopeMapper
{
    /// <summary>
    ///     Read the pushed request into an envelope. Set either <see cref="Envelope.Message" />, or
    ///     <see cref="Envelope.Data" /> with <see cref="Envelope.MessageType" /> and <see cref="Envelope.ContentType" />
    ///     for Wolverine to deserialize. Set <see cref="Envelope.Id" /> from the broker's message id when it has one, so
    ///     the optional inbox deduplication recognizes a redelivery. Return null when the request does not carry a
    ///     message, and the receiver answers 400 without handling anything
    /// </summary>
    ValueTask<Envelope?> MapAsync(HttpContext context, CancellationToken cancellation);
}
