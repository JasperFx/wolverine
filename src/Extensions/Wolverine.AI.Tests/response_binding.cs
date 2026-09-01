using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Wolverine.AI.Internals;

namespace Wolverine.AI.Tests;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IncidentTriage))]
[JsonSerializable(typeof(IncidentSnapshot))]
internal partial class TestJsonContext : JsonSerializerContext;

/// <summary>
/// GH-4230. The three trim-sensitive operations, exercised against both a reflection-based serializer
/// and a source-generated one — an application on either footing has to get identical behaviour, or the
/// AOT path is a different feature wearing the same API.
/// </summary>
public class response_binding
{
    private static LlmCalloutOptions reflectionBased() => new();

    private static LlmCalloutOptions sourceGenerated()
    {
        var options = new LlmCalloutOptions
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = TestJsonContext.Default
            }
        };

        options.RegisterResponseType<IncidentTriage>();

        return options;
    }

    public static TheoryData<string, LlmCalloutOptions> Both => new()
    {
        { "reflection", reflectionBased() },
        { "source-generated", sourceGenerated() }
    };

    [Theory]
    [MemberData(nameof(Both))]
    public void builds_a_schema_carrying_the_response_type_properties(string _, LlmCalloutOptions options)
    {
        var schema = new LlmResponseBinder(options).SchemaFor(typeof(IncidentTriage)).ToString();

        // Asserting on the contents, not merely that the call returned: CreateJsonSchema answers an
        // empty schema rather than throwing when the type info resolver cannot see the type, which
        // would hand the model a constraint that constrains nothing.
        schema.ShouldContain("severity");
        schema.ShouldContain("recommendedAction");
    }

    [Theory]
    [MemberData(nameof(Both))]
    public void reads_an_answer_back_into_the_response_type(string _, LlmCalloutOptions options)
    {
        var answer = """{"severity":"high","recommendedAction":"page the on-call"}""";

        var value = new LlmResponseBinder(options).Deserialize(answer, typeof(IncidentTriage));

        value.ShouldBeOfType<IncidentTriage>().Severity.ShouldBe("high");
    }

    [Fact]
    public void a_registered_response_type_resolves_without_reflection()
    {
        var options = sourceGenerated();
        var identifier = LlmCallout.Ask<IncidentTriage>("triage").ResponseType!;

        new LlmResponseBinder(options).TryResolveRegisteredType(identifier, out var type).ShouldBeTrue();
        type.ShouldBe(typeof(IncidentTriage));
    }

    [Fact]
    public void an_unregistered_response_type_does_not_resolve_through_the_table()
    {
        // The trimmed application's failure mode, and it must be a clean miss rather than a throw, so
        // the executor can fall through to the reflective path an untrimmed application still wants.
        new LlmResponseBinder(sourceGenerated())
            .TryResolveRegisteredType(LlmCallout.Ask<IncidentSnapshot>("x").ResponseType!, out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void WithContext_takes_pre_serialized_json()
    {
        LlmCallout.Ask<IncidentTriage>("triage").WithContext("""{"id":"INC-1"}""")
            .Context.ShouldBe("""{"id":"INC-1"}""");
    }

    [Fact]
    public void WithContext_serializes_through_source_generated_type_info()
    {
        var callout = LlmCallout.Ask<IncidentTriage>("triage")
            .WithContext(new IncidentSnapshot("INC-1", "on fire", 12), TestJsonContext.Default.IncidentSnapshot);

        callout.Context.ShouldNotBeNull();
        callout.Context.ShouldContain("INC-1");

        // Same shape as the reflective overload produces, so moving an application onto the AOT path
        // does not silently change what the model is shown.
        var reflective = LlmCallout.Ask<IncidentTriage>("triage", new IncidentSnapshot("INC-1", "on fire", 12));
        callout.Context.ShouldBe(reflective.Context);
    }
}
