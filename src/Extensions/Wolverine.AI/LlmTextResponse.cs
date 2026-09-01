namespace Wolverine.AI;

/// <summary>
/// The answer to a text flavour callout — one built with <see cref="LlmCallout.Ask(string)" /> rather
/// than <see cref="LlmCallout.Ask{TResponse}(string)" />. Published as an ordinary message.
/// </summary>
/// <remarks>
/// Every text callout in an application publishes this same type, so a handler that cares about only
/// one kind of callout switches on <see cref="LlmCallout.Tag" /> through <see cref="Callout" />. Where
/// that starts to feel like a switch statement pretending to be a type, that is the signal to move to
/// the structured flavour and let each answer be its own message.
/// </remarks>
/// <param name="Text">The model's answer.</param>
/// <param name="Callout">The callout that produced it, for correlation and for its tag.</param>
public record LlmTextResponse(string Text, LlmCallout Callout);
