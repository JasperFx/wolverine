using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.AI.Testing;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace Wolverine.AI.Tests;

/// <summary>
/// CritterWatch#1524 — callouts under <c>TypeLoadMode.Static</c> when the application's pre-generated code does not
/// include the callout chain. That is the shape of a library that ships its own static codegen (CritterWatch) and a host
/// that adds AI to it: before this, <c>AddLlmCallouts</c> made the host fail to start with
/// <c>MissingPreBuiltTypesException</c> naming "LlmCallout handled by LlmCalloutHandler.HandleAsync()".
/// </summary>
/// <remarks>
/// Conventional discovery is off and the application assembly is this test assembly, which carries no generated code at
/// all, so any chain that needs a pre-built type fails the start. The answer is received by a pre-built handler for the
/// same reason.
/// </remarks>
public class static_type_load_mode
{
    private readonly StubChatClient _chat = new();
    private readonly List<object> _answers = new();

    private sealed class AnswerRecorder(List<object> answers) : MessageHandler<IncidentTriage>
    {
        protected override Task HandleAsync(IncidentTriage message, MessageContext context, CancellationToken cancellation)
        {
            lock (answers) answers.Add(message);
            return Task.CompletedTask;
        }
    }

    private Task<IHost> staticHostFor(Action<LlmCalloutOptions>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(static_type_load_mode).Assembly;
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

                opts.Services.AddSingleton<IChatClient>(_chat);
                opts.AddMessageHandler(new AnswerRecorder(_answers));
                // A pre-built handler is not found by the local routing conventions; route the answer to it.
                opts.PublishMessage<IncidentTriage>().ToLocalQueue("answers");
                opts.AddLlmCallouts(ai =>
                {
                    ai.DurableQueue = false;
                    ai.RetryCooldowns = [];
                    configure?.Invoke(ai);
                });
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_static_host_with_no_generated_callout_chain_starts_and_runs_callouts()
    {
        _chat.Respond(new IncidentTriage("high", "page the on-call"));

        using var host = await staticHostFor();

        await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<IncidentTriage>(host)
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("triage this", new IncidentSnapshot("INC-1", "on fire", 3)));

        lock (_answers) _answers.OfType<IncidentTriage>().ShouldHaveSingleItem().Severity.ShouldBe("high");
        _chat.Requests.ShouldHaveSingleItem();

        var handler = host.Services.GetRequiredService<IWolverineRuntime>().Options.HandlerGraph.HandlerFor(typeof(LlmCallout));
        handler.ShouldNotBeNull().GetType().Name.ShouldBe("LlmCalloutMessageHandler",
            "Static mode runs the pre-built handler, which needs no generated code");
    }

    [Fact]
    public async Task the_budget_is_enforced_and_dead_letters_without_calling_the_model()
    {
        using var host = await staticHostFor(ai => ai.Budget.MaximumPromptCharacters = 5);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("triage this, at some length"));

        session.MovedToErrorQueue.MessagesOf<LlmCallout>().ShouldHaveSingleItem();
        _chat.Requests.ShouldBeEmpty("the budget refuses before the provider is called");
    }

    [Fact]
    public async Task an_unparseable_answer_is_dead_lettered_rather_than_retried()
    {
        _chat.Respond("I'm afraid I can't do that, Dave.");

        using var host = await staticHostFor(ai => ai.RetryCooldowns = [1.Seconds()]);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("triage this"));

        session.MovedToErrorQueue.MessagesOf<LlmCallout>().ShouldHaveSingleItem();
        _chat.Requests.Count.ShouldBe(1, "Wolverine.AI's own rules apply to the pre-built handler's chain");
    }

    [Fact]
    public async Task ConfigureChain_applies_to_the_pre_built_handler()
    {
        _chat.Throw(new TimeoutException("the model is slow today"));

        using var host = await staticHostFor(ai => ai.ConfigureChain(chain => chain.OnException<TimeoutException>().Discard()));

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("triage this"));

        session.Discarded.MessagesOf<LlmCallout>().ShouldHaveSingleItem();
        session.MovedToErrorQueue.MessagesOf<LlmCallout>().ShouldBeEmpty();
    }

    [Fact]
    public async Task ConfigureChain_applies_to_the_generated_chain_too()
    {
        _chat.Throw(new TimeoutException("the model is slow today"));

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IChatClient>(_chat);
                opts.AddLlmCallouts(ai =>
                {
                    ai.DurableQueue = false;
                    ai.RetryCooldowns = [];
                    ai.ConfigureChain(chain => chain.OnException<TimeoutException>().Discard());
                });
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("triage this"));

        session.Discarded.MessagesOf<LlmCallout>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task deduplication_is_refused_at_start_rather_than_silently_skipped()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => staticHostFor(ai => ai.DeduplicateCallouts = true));

        ex.Message.ShouldContain(nameof(LlmCalloutOptions.DeduplicateCallouts));
    }
}
