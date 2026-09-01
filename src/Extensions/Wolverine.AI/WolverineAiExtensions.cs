using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.AI.Internals;

namespace Wolverine.AI;

public static class WolverineAiExtensions
{
    /// <summary>
    /// Turn on Wolverine's durable LLM callout support: <see cref="LlmCallout" /> becomes a message that
    /// executes on its own local queue against the <see cref="IChatClient" /> registered in the
    /// application's container, and publishes the model's answer as an ordinary cascading message.
    ///
    /// <para>
    /// Registering the <see cref="IChatClient" /> itself stays with the application. WolverineFx.AI
    /// depends only on the Microsoft.Extensions.AI abstractions, so the provider — Anthropic, OpenAI,
    /// Azure, Ollama — and any middleware over it are yours to choose:
    /// </para>
    ///
    /// <example>
    /// <code>
    /// builder.Services.AddChatClient(new AnthropicClient(key).AsIChatClient())
    ///     .UseOpenTelemetry()
    ///     .UseDistributedCache();
    ///
    /// builder.UseWolverine(opts =>
    /// {
    ///     opts.AddLlmCallouts(ai =>
    ///     {
    ///         ai.DefaultModelId = "claude-sonnet-5";
    ///         ai.Budget.MaximumTokensPerWindow = 200_000;
    ///     });
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static WolverineOptions AddLlmCallouts(this WolverineOptions options,
        Action<LlmCalloutOptions>? configure = null)
    {
        if (options.Services.Any(x => x.ServiceType == typeof(LlmCalloutOptions)))
        {
            throw new InvalidOperationException(
                $"{nameof(AddLlmCallouts)}() has already been called on this application. Configure the callout " +
                "queue, budget, and defaults in a single call.");
        }

        var ai = new LlmCalloutOptions();
        configure?.Invoke(ai);

        options.Services.AddSingleton(ai);
        options.Services.AddSingleton<ILlmBudgetLedger>(new LlmBudgetLedger(ai.Budget));
        options.Services.TryAddSingleton<ILlmCalloutExecutor, LlmCalloutExecutor>();

        // The handler lives in WolverineFx.AI rather than in the application's own assemblies, so
        // conventional discovery will never see it.
        options.Discovery.IncludeType(typeof(LlmCalloutHandler));

        var queue = options.LocalQueue(LlmCallout.QueueName)
            .MaximumParallelMessages(ai.MaximumParallelCallouts);

        // Durable is the point: a callout returned from a handler is enrolled in that handler's outbox,
        // so it cannot fire for a transaction that did not commit and cannot be lost to a restart in
        // between. Buffered is the fallback for applications with no message persistence at all.
        if (ai.DurableQueue)
        {
            queue.UseDurableInbox();
        }
        else
        {
            queue.BufferedInMemory();
        }

        ai.ApplyQueueConfiguration(queue);

        if (ai.Budget.IsEnabled)
        {
            options.Policies.ForMessagesOfType<LlmCallout>().AddMiddleware(typeof(LlmBudgetMiddleware));
        }

        options.Policies.Add(new LlmCalloutChainPolicy(ai));

        // Checked lazily rather than here, because registering the chat client after AddLlmCallouts() is
        // perfectly reasonable. Failing at bootstrap beats failing on the first callout: without this the
        // application starts clean and only falls over when the model is first needed, in a handler, as a
        // container resolution error that says nothing about AI.
        options.ConfigureLazily(o =>
        {
            if (o.Services.Any(x => x.ServiceType == typeof(IChatClient))) return;

            throw new InvalidOperationException(
                $"{nameof(AddLlmCallouts)}() was called, but no {nameof(IChatClient)} is registered in this " +
                "application's container. Register one from your provider's Microsoft.Extensions.AI adapter, " +
                "e.g. builder.Services.AddChatClient(...), or use Wolverine.AI.Testing.StubChatClient in tests.");
        });

        return options;
    }
}
