using System.Text.Json;
using Wolverine.Runtime.Serialization;

namespace Wolverine.AI.Tests;

/// <summary>
/// The reason <see cref="LlmCallout" /> is not generic (GH-4227): a callout has to survive being
/// written to a durable inbox, read back after a process restart, and executed — with nothing having
/// been registered first, because on a cold start the recovery sweep runs before the application has
/// published a single callout of its own.
/// </summary>
public class durable_round_trip
{
    private static readonly SystemTextJsonSerializer _serializer = new(new JsonSerializerOptions());

    [Fact]
    public void a_callout_survives_serialization()
    {
        var callout = LlmCallout
            .Ask<IncidentTriage>("Triage this incident.", new IncidentSnapshot("INC-1", "on fire", 12))
            .WithSystemPrompt("You are an SRE.")
            .UsingModel("claude-sonnet-5")
            .WithTemperature(0.2f)
            .WithMaxOutputTokens(512)
            .Tagged("triage")
            .DeduplicatedBy("INC-1:7");

        var envelope = new Envelope(callout);
        var read = (LlmCallout)_serializer.ReadFromData(typeof(LlmCallout),
            new Envelope { Data = _serializer.Write(envelope) });

        read.Prompt.ShouldBe(callout.Prompt);
        read.Context.ShouldBe(callout.Context);
        read.SystemPrompt.ShouldBe("You are an SRE.");
        read.ResponseType.ShouldBe(callout.ResponseType);
        read.ModelId.ShouldBe("claude-sonnet-5");
        read.Temperature.ShouldBe(0.2f);
        read.MaxOutputTokens.ShouldBe(512);
        read.Tag.ShouldBe("triage");
        read.DeduplicationId.ShouldBe("INC-1:7");

        read.ExpectsResponse<IncidentTriage>().ShouldBeTrue();
    }

    [Fact]
    public void the_response_type_resolves_from_the_wire_value_alone()
    {
        // Written by hand rather than through Ask<T>() on purpose: this is what a callout recovered
        // from the inbox looks like, and nothing in this process has mentioned IncidentTriage to
        // Wolverine yet. A generic LlmCallout<IncidentTriage> would have nothing to resolve here.
        var identifier = $"{typeof(IncidentTriage).FullName}, Wolverine.AI.Tests";

        Type.GetType(identifier).ShouldBe(typeof(IncidentTriage));
    }

    [Fact]
    public void a_callout_has_a_single_stable_message_type_name()
    {
        // One name for every callout in the application, which is what gives the callout queue one
        // handler chain, one dead letter identity, and one place for the budget middleware.
        LlmCallout.Ask<IncidentTriage>("a").GetType()
            .ShouldBe(LlmCallout.Ask<IncidentSnapshot>("b").GetType());
    }
}
