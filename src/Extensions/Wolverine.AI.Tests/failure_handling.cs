using JasperFx.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.AI.Testing;
using Wolverine.Tracking;

namespace Wolverine.AI.Tests;

public class failure_handling
{
    private readonly StubChatClient _chat = new();

    private Task<IHost> hostFor(Action<LlmCalloutOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IChatClient>(_chat);
                opts.AddLlmCallouts(ai =>
                {
                    ai.DurableQueue = false;
                    configure(ai);
                });
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_unparseable_answer_is_dead_lettered_rather_than_retried()
    {
        _chat.Respond("I'm afraid I can't do that, Dave.");

        using var host = await hostFor(_ => { });

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("triage this"));

        session.MovedToErrorQueue.MessagesOf<LlmCallout>().ShouldHaveSingleItem();

        var exception = session.AllExceptions().OfType<LlmCalloutException>().ShouldHaveSingleItem();
        exception.RawResponse.ShouldBe("I'm afraid I can't do that, Dave.");

        // One attempt, not four: retrying sends the identical prompt and gets the identical
        // unusable answer, so the retry schedule must not apply here.
        _chat.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_transient_failure_is_retried_on_the_cooldown_schedule()
    {
        _chat.Throw(new HttpRequestException("503 from the provider"));
        _chat.Respond(new IncidentTriage("high", "page the on-call"));

        using var host = await hostFor(ai => ai.RetryCooldowns = [1.Milliseconds()]);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("triage this"));

        session.MovedToErrorQueue.MessagesOf<LlmCallout>().ShouldBeEmpty();
        _chat.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task an_over_long_prompt_is_refused_before_the_model_is_called()
    {
        using var host = await hostFor(ai => ai.Budget.MaximumPromptCharacters = 50);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>(new string('x', 500)));

        session.MovedToErrorQueue.MessagesOf<LlmCallout>().ShouldHaveSingleItem();
        session.AllExceptions().OfType<LlmBudgetExceededException>().ShouldHaveSingleItem();

        // The point of the guard: the provider is never called, so the runaway prompt costs nothing.
        _chat.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_budget_counts_the_context_that_gets_appended_to_the_prompt()
    {
        using var host = await hostFor(ai => ai.Budget.MaximumPromptCharacters = 30);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("triage",
                new IncidentSnapshot("INC-1", "a long enough summary to blow the budget", 4)));

        session.AllExceptions().OfType<LlmBudgetExceededException>().ShouldHaveSingleItem();
        _chat.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_spent_token_budget_refuses_the_next_callout()
    {
        _chat.InputTokenCount = 60;
        _chat.OutputTokenCount = 60;
        _chat.Respond(new IncidentTriage("high", "page"));

        using var host = await hostFor(ai =>
        {
            ai.Budget.MaximumTokensPerWindow = 100;
            ai.Budget.Window = 5.Minutes();
        });

        // The first callout is under budget when it starts, and spends 120 tokens.
        await host.SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("first"));

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("second"));

        session.AllExceptions().OfType<LlmBudgetExceededException>().ShouldHaveSingleItem();
        _chat.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_callout_that_outlives_its_timeout_is_reported_as_a_timeout()
    {
        _chat.RespondAfter(5.Seconds(), new IncidentTriage("high", "page"));

        using var host = await hostFor(ai =>
        {
            ai.Timeout = 20.Milliseconds();
            ai.RetryCooldowns = [];
        });

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(LlmCallout.Ask<IncidentTriage>("triage this"));

        session.AllExceptions().OfType<TimeoutException>().ShouldNotBeEmpty();
    }
}
