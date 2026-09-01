using JasperFx.Core;

namespace Wolverine.AI.Internals;

/// <summary>
/// Composes the single user message sent for a callout. Shared by the executor and the budget
/// middleware so that the character count the budget enforces is the count of what is actually sent,
/// not of the prompt before its context was appended.
/// </summary>
internal static class LlmCalloutPrompt
{
    public static string Compose(LlmCallout callout)
    {
        return callout.Context.IsEmpty()
            ? callout.Prompt
            : $"{callout.Prompt}{Environment.NewLine}{Environment.NewLine}{callout.Context}";
    }
}
