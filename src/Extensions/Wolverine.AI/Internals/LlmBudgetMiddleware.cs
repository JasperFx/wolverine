using JasperFx.Core;

namespace Wolverine.AI.Internals;

/// <summary>
/// Enforces <see cref="LlmBudget" /> before a callout reaches the model. Applied as ordinary Wolverine
/// middleware on the callout chain, so it shows up in <c>codegen describe</c> alongside everything else
/// and an application can see exactly where its spend guard sits.
/// </summary>
public static class LlmBudgetMiddleware
{
    public static void Before(LlmCallout callout, LlmCalloutOptions options, ILlmBudgetLedger ledger)
    {
        var budget = options.Budget;

        if (budget.MaximumPromptCharacters is { } maximumCharacters)
        {
            var length = LlmCalloutPrompt.Compose(callout).Length;
            if (length > maximumCharacters)
            {
                throw new LlmBudgetExceededException(
                    $"{callout} composes a {length} character prompt, over the configured " +
                    $"LlmBudget.MaximumPromptCharacters of {maximumCharacters}.");
            }
        }

        if (budget.MaximumTokensPerWindow is { } maximumTokens)
        {
            var spent = ledger.TokensInWindow();
            if (spent >= maximumTokens)
            {
                throw new LlmBudgetExceededException(
                    $"{callout} was refused: this node has spent {spent} tokens in the trailing " +
                    $"{budget.Window.ToDisplay()}, at or over the configured LlmBudget.MaximumTokensPerWindow " +
                    $"of {maximumTokens}.");
            }
        }
    }
}
