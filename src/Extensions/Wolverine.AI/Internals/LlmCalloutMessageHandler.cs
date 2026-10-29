using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.AI.Internals;

/// <summary>
/// The callout handler under <c>TypeLoadMode.Static</c>: a pre-built <see cref="MessageHandler{T}" /> that needs no
/// generated code. CritterWatch#1524.
/// </summary>
/// <remarks>
/// <para>
/// In <c>TypeLoadMode.Static</c> every discovered chain must have a pre-generated type in the application assembly,
/// and <see cref="LlmCalloutHandler" />'s chain has one only if <c>codegen write</c> ran with <c>AddLlmCallouts</c>
/// configured. A library that ships its own static codegen (CritterWatch) cannot know whether its host will add AI, so
/// that chain was missing and the host failed to start. This handler is registered instead of the discovered one in
/// Static mode, and does by hand what the generated chain does: the budget check, the call, the cascading answer.
/// </para>
/// <para>
/// ⚠️ Handler policies do not reach a pre-built handler's chain, so its rules are applied here, in
/// <see cref="ConfigureChain" />: Wolverine.AI's own, then <see cref="LlmCalloutOptions.ConfigureChain" />.
/// </para>
/// </remarks>
internal sealed class LlmCalloutMessageHandler : MessageHandler<LlmCallout>
{
    private readonly LlmCalloutOptions _options;

    public LlmCalloutMessageHandler(LlmCalloutOptions options)
    {
        _options = options;
    }

    public override void ConfigureChain(HandlerChain chain)
    {
        LlmCalloutChainPolicy.ApplyRules(chain, _options);
        _options.ApplyChainConfiguration(chain);
    }

    protected override async Task HandleAsync(LlmCallout message, MessageContext context, CancellationToken cancellation)
    {
        var services = context.Runtime.Services;

        // The generated chain weaves this in as middleware only when a budget is configured; it is a no-op without one.
        if (_options.Budget.IsEnabled)
        {
            LlmBudgetMiddleware.Before(message, _options, services.GetRequiredService<ILlmBudgetLedger>());
        }

        var answer = await services.GetRequiredService<ILlmCalloutExecutor>().ExecuteAsync(message, cancellation);

        // Exactly what returning the answer from the generated chain does: an ordinary cascading message.
        await context.EnqueueCascadingAsync(answer);
    }
}
