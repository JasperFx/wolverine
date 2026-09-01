using JasperFx.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.AI.Testing;
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
