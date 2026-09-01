using Wolverine.Attributes;

namespace Wolverine.AI.Tests;

public class callout_specs
{
    [Fact]
    public void ask_captures_the_response_type()
    {
        var callout = LlmCallout.Ask<IncidentTriage>("triage this");

        callout.Prompt.ShouldBe("triage this");
        callout.Context.ShouldBeNull();
        callout.ExpectsResponse<IncidentTriage>().ShouldBeTrue();
        callout.ExpectsResponse<IncidentSnapshot>().ShouldBeFalse();
    }

    [Fact]
    public void the_text_flavour_expects_no_response_type()
    {
        var callout = LlmCallout.Ask("summarize this");

        callout.ResponseType.ShouldBeNull();
        callout.ExpectsResponse<IncidentTriage>().ShouldBeFalse();
    }

    [Fact]
    public void context_is_serialized_to_json()
    {
        var callout = LlmCallout.Ask<IncidentTriage>("triage this", new IncidentSnapshot("INC-1", "fire", 3));

        callout.Context.ShouldNotBeNull();
        callout.Context.ShouldContain("INC-1");
        callout.Context.ShouldContain("minutesOpen");
    }

    [Fact]
    public void context_is_serialized_compactly()
    {
        // Every space and newline in a prompt is a billed input token, and also counts against
        // LlmBudget.MaximumPromptCharacters. AIJsonUtilities.DefaultOptions sets WriteIndented, so the
        // context serializer deliberately does not use it. See GH-4230.
        var context = LlmCallout
            .Ask<IncidentTriage>("triage", new IncidentSnapshot("INC-1", "on fire", 12))
            .Context.ShouldNotBeNull();

        context.ShouldNotContain("\n");
        context.ShouldBe("""{"incidentId":"INC-1","summary":"on fire","minutesOpen":12}""");
    }

    [Fact]
    public void an_ordinary_response_type_is_identified_by_its_assembly_qualified_name()
    {
        // Assembly qualified rather than Wolverine's message type alias, so that a callout recovered
        // from a durable inbox on a cold start resolves with nothing having been registered first.
        LlmCallout.IdentifierFor(typeof(IncidentTriage))
            .ShouldBe($"{typeof(IncidentTriage).FullName}, Wolverine.AI.Tests");

        Type.GetType(LlmCallout.IdentifierFor(typeof(IncidentTriage))).ShouldBe(typeof(IncidentTriage));
    }

    [Fact]
    public void a_response_type_with_MessageIdentity_is_identified_by_its_stable_alias()
    {
        LlmCallout.IdentifierFor(typeof(RenameProofTriage)).ShouldBe("renamed-proof-triage");
    }

    [Fact]
    public void fluent_configuration_sets_every_knob()
    {
        var callout = LlmCallout.Ask<IncidentTriage>("triage this")
            .WithSystemPrompt("you are an SRE")
            .UsingModel("claude-sonnet-5")
            .WithTemperature(0.2f)
            .WithMaxOutputTokens(256)
            .Tagged("triage")
            .DeduplicatedBy("INC-1:7");

        callout.SystemPrompt.ShouldBe("you are an SRE");
        callout.ModelId.ShouldBe("claude-sonnet-5");
        callout.Temperature.ShouldBe(0.2f);
        callout.MaxOutputTokens.ShouldBe(256);
        callout.Tag.ShouldBe("triage");
        callout.DeduplicationId.ShouldBe("INC-1:7");
    }

    [Fact]
    public void to_string_names_the_tag_and_the_expected_answer()
    {
        LlmCallout.Ask("summarize").ToString().ShouldBe("LlmCallout expecting text");

        LlmCallout.Ask<IncidentTriage>("triage").Tagged("triage").ToString()
            .ShouldStartWith("LlmCallout 'triage' expecting ");
    }
}

[MessageIdentity("renamed-proof-triage")]
public record RenameProofTriage(string Severity);
