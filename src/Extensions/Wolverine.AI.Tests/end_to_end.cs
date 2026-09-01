using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.AI.Testing;
using Wolverine.Tracking;

namespace Wolverine.AI.Tests;

public class end_to_end : IAsyncLifetime
{
    private readonly StubChatClient _chat = new();
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        TriageResults.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IChatClient>(_chat);

                opts.AddLlmCallouts(ai =>
                {
                    // No message store in these tests, so the callout queue cannot be durable. The
                    // durable path is covered by durable_callout_queue.cs against Postgres.
                    ai.DurableQueue = false;
                    ai.DefaultModelId = "stub-model";
                });
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TestContext.Current.CancellationToken);
        _host.Dispose();
    }

    [Fact]
    public async Task the_callout_is_published_as_a_cascading_message()
    {
        _chat.Respond(new IncidentTriage("high", "page the on-call"));

        var session = await _host.InvokeMessageAndWaitAsync(new AlertRaised("INC-1", "database is on fire"));

        var callout = session.Sent.SingleMessage<LlmCallout>();
        callout.ExpectsResponse<IncidentTriage>().ShouldBeTrue();
        callout.Tag.ShouldBe("triage");
        callout.Prompt.ShouldBe("Triage this incident.");
    }

    [Fact]
    public async Task the_answer_comes_back_as_an_ordinary_typed_message()
    {
        _chat.Respond(new IncidentTriage("high", "page the on-call"));

        await _host.InvokeMessageAndWaitAsync(new AlertRaised("INC-1", "database is on fire"));

        var triage = TriageResults.Received.OfType<IncidentTriage>().ShouldHaveSingleItem();
        triage.Severity.ShouldBe("high");
        triage.RecommendedAction.ShouldBe("page the on-call");
    }

    [Fact]
    public async Task the_callout_context_is_appended_underneath_the_prompt()
    {
        _chat.Respond(new IncidentTriage("low", "wait"));

        await _host.InvokeMessageAndWaitAsync(new AlertRaised("INC-7", "disk nearly full"));

        var request = _chat.Requests.ShouldHaveSingleItem();
        request.Prompt.ShouldStartWith("Triage this incident.");
        request.Prompt.ShouldContain("INC-7");
        request.Prompt.ShouldContain("disk nearly full");
    }

    [Fact]
    public async Task a_structured_callout_asks_the_model_for_a_json_schema()
    {
        _chat.Respond(new IncidentTriage("low", "wait"));

        await _host.InvokeMessageAndWaitAsync(new AlertRaised("INC-7", "disk nearly full"));

        var request = _chat.Requests.ShouldHaveSingleItem();
        request.IsStructured.ShouldBeTrue();

        var format = request.Options!.ResponseFormat.ShouldBeOfType<ChatResponseFormatJson>();
        format.SchemaName.ShouldBe(nameof(IncidentTriage));
        format.Schema!.Value.ToString().ShouldContain("recommendedAction");
    }

    [Fact]
    public async Task the_configured_default_model_is_used()
    {
        _chat.Respond(new IncidentTriage("low", "wait"));

        await _host.InvokeMessageAndWaitAsync(new AlertRaised("INC-7", "disk nearly full"));

        _chat.Requests.ShouldHaveSingleItem().Options!.ModelId.ShouldBe("stub-model");
    }

    [Fact]
    public async Task the_text_flavour_publishes_an_LlmTextResponse()
    {
        _chat.Respond("Everything is fine.");

        var callout = LlmCallout.Ask("Summarize the last hour.").Tagged("summary");

        await _host.SendMessageAndWaitAsync(callout);

        var response = TriageResults.Received.OfType<LlmTextResponse>().ShouldHaveSingleItem();
        response.Text.ShouldBe("Everything is fine.");
        response.Callout.Tag.ShouldBe("summary");

        _chat.Requests.ShouldHaveSingleItem().IsStructured.ShouldBeFalse();
    }

    [Fact]
    public async Task a_per_callout_model_and_system_prompt_override_the_defaults()
    {
        _chat.Respond("ok");

        await _host.SendMessageAndWaitAsync(LlmCallout.Ask("Summarize.")
            .UsingModel("some-other-model")
            .WithSystemPrompt("You are terse.")
            .WithTemperature(0.1f)
            .WithMaxOutputTokens(64));

        var options = _chat.Requests.ShouldHaveSingleItem().Options!;
        options.ModelId.ShouldBe("some-other-model");
        options.Instructions.ShouldBe("You are terse.");
        options.Temperature.ShouldBe(0.1f);
        options.MaxOutputTokens.ShouldBe(64);
    }
}
