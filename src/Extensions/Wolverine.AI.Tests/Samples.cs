using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.AI.Testing;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace Wolverine.AI.Tests;

#region sample_llm_callout_bootstrapping

public static class Bootstrapping
{
    public static async Task<IHost> BuildHost(IChatClient chatClient)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Registering the IChatClient is yours. Wolverine.AI only references the
                // Microsoft.Extensions.AI abstractions, so the provider -- and any middleware
                // over it, like UseOpenTelemetry() or UseDistributedCache() -- is your choice.
                opts.Services.AddSingleton(chatClient);

                opts.AddLlmCallouts(ai =>
                {
                    ai.DefaultModelId = "claude-sonnet-5";
                    ai.DefaultSystemPrompt = "You are an experienced site reliability engineer.";

                    // Back pressure: at most this many calls to the model are in flight at once,
                    // no matter how fast callouts are published.
                    ai.MaximumParallelCallouts = 5;

                    // Spend guardrails, enforced as middleware on the callout queue.
                    ai.Budget.MaximumPromptCharacters = 20_000;
                    ai.Budget.MaximumTokensPerWindow = 200_000;
                    ai.Budget.Window = 1.Minutes();
                });
            }).StartAsync();
    }
}

#endregion

public record Incident(string Id, string Summary, bool JustEscalated)
{
    public IncidentSnapshot Snapshot() => new(Id, Summary, 12);
}

public record AlertSeen(string IncidentId);

public static class Prompts
{
    public const string Triage =
        "Classify the severity of this incident and recommend a single next action.";
}

#region sample_llm_callout_from_a_handler

public static class AlertSeenHandler
{
    // The callout is returned as a cascading message next to the storage action, so it is enrolled
    // in the same outbox as the write: a callout cannot fire for a transaction that did not commit.
    public static (IStorageAction<Incident>, LlmCallout) Handle(AlertSeen message, Incident incident)
    {
        return (Storage.Update(incident),
            LlmCallout.Ask<IncidentTriage>(Prompts.Triage, incident.Snapshot()).Tagged("triage"));
    }
}

// The answer arrives as an ordinary message, with an ordinary handler, an ordinary retry policy,
// and its own place in the correlation chain.
public static class TriageHandler
{
    public static void Handle(IncidentTriage triage)
    {
        // page someone, open a ticket, whatever the severity calls for
    }
}

#endregion

public class SampleTests
{
    #region sample_llm_callout_testing

    [Fact]
    public async Task triage_an_escalated_incident()
    {
        // A scripted IChatClient: no key, no network, no model.
        var chat = new StubChatClient()
            .Respond(new IncidentTriage("high", "page the on-call"));

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IChatClient>(chat);
                opts.AddLlmCallouts(ai => ai.DurableQueue = false);
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await host.InvokeMessageAndWaitAsync(new AlertRaised("INC-1", "database is on fire"));

        // Assert on the callout the handler produced...
        var callout = session.Sent.SingleMessage<LlmCallout>();
        callout.ExpectsResponse<IncidentTriage>().ShouldBeTrue();
        callout.Tag.ShouldBe("triage");

        // ...on what was actually sent to the model...
        chat.Requests.ShouldHaveSingleItem().Prompt.ShouldContain("INC-1");

        // ...and on the answer coming back as an ordinary message.
        session.Received.SingleMessage<IncidentTriage>().Severity.ShouldBe("high");
    }

    #endregion

    #region sample_llm_callout_unit_test

    [Fact]
    public void the_handler_asks_for_a_triage()
    {
        var incident = new Incident("INC-1", "database is on fire", true);

        var (_, callout) = AlertSeenHandler.Handle(new AlertSeen("INC-1"), incident);

        callout.ExpectsResponse<IncidentTriage>().ShouldBeTrue();
        callout.Prompt.ShouldBe(Prompts.Triage);
        callout.Context.ShouldNotBeNull().ShouldContain("INC-1");
    }

    #endregion
}

#region sample_llm_callout_queue_configuration

public static class QueueConfiguration
{
    public static void Configure(WolverineOptions opts)
    {
        opts.AddLlmCallouts(ai =>
        {
            // The back pressure knob. Five callouts may be talking to the model at once; the sixth
            // waits its turn on the queue instead of piling another request onto your provider.
            ai.MaximumParallelCallouts = 3;

            // How long any single callout may run before it is cancelled and retried.
            ai.Timeout = 90.Seconds();

            // Anything else you would do to a local queue, you can still do here.
            ai.ConfigureQueue(queue =>
            {
                // Stop calling the model at all once it starts failing consistently, instead of
                // burning the retry schedule on a provider that is plainly down.
                queue.CircuitBreaker(cb =>
                {
                    cb.MinimumThreshold = 10;
                    cb.FailurePercentageThreshold = 20;
                    cb.PauseTime = 2.Minutes();
                });
            });
        });
    }
}

#endregion

#region sample_llm_callout_sequential_queue

public static class SequentialCallouts
{
    public static void Configure(WolverineOptions opts)
    {
        // A provider on a tight per-account rate limit, or a model you are only allowed one
        // conversation with at a time: process callouts strictly one at a time, in order.
        opts.AddLlmCallouts(ai => ai.ConfigureQueue(queue => queue.Sequential()));
    }
}

#endregion

#region sample_llm_response_queue_configuration

public static class ResponseQueueConfiguration
{
    public static void Configure(WolverineOptions opts)
    {
        opts.AddLlmCallouts(ai => ai.MaximumParallelCallouts = 10);

        // The answer is an ordinary message, so the queue it lands on is configured the ordinary
        // way. Ten callouts can be in flight against the model while the work their answers kick
        // off -- paging someone, writing to a downstream system -- runs one at a time.
        opts.LocalQueueFor<IncidentTriage>()
            .Sequential()
            .UseDurableInbox();
    }
}

#endregion

#region sample_llm_callout_error_handling

// The LlmCallout handler ships inside WolverineFx.AI, so you cannot drop a Configure(HandlerChain)
// method onto it the way you would with one of your own handlers. An IHandlerPolicy reaches the same
// chain, and it is exactly how Wolverine.AI applies its own defaults.
public class CalloutErrorPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(x => x.MessageType == typeof(LlmCallout)))
        {
            // A provider rate limit is worth waiting out rather than hammering.
            chain.OnException<HttpRequestException>()
                .RetryWithCooldown(5.Seconds(), 30.Seconds(), 2.Minutes());

            // A callout that timed out may well have cost you a completion you never saw. Give it one
            // more attempt, then get it out of the queue rather than paying for it a third time.
            chain.OnException<TaskCanceledException>()
                .RetryOnce()
                .Then.MoveToErrorQueue();
        }
    }
}

public static class CalloutErrorHandling
{
    public static void Configure(WolverineOptions opts)
    {
        // Opt out of the built in cooldown schedule so your rules are the whole story. Leave this
        // alone and both sets apply, with Wolverine.AI's defaults added first.
        opts.AddLlmCallouts(ai => ai.RetryCooldowns = []);

        opts.Policies.Add(new CalloutErrorPolicy());
    }
}

#endregion

#region sample_llm_response_error_handling

// The answer is an ordinary message with an ordinary handler, so its error handling is configured the
// ordinary way -- and separately from the callout's. That separation is the point: a failure in your
// own downstream work should not send you back to the provider for a second bill.
public record OutageSummary(string Headline, string NextStep);

public static class OutageSummaryHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<TimeoutException>()
            .RetryWithCooldown(1.Seconds(), 5.Seconds());

        chain.OnException<InvalidOperationException>()
            .MoveToErrorQueue();
    }

    public static void Handle(OutageSummary summary)
    {
        // file the incident report, notify the channel, whatever the summary calls for
    }
}

#endregion
