using System.Text.Json;
using System.Text.Json.Serialization;
using Wolverine.AI;
using Wolverine.AI.Internals;

namespace Wolverine.AI.AotSmoke;

public record IncidentSnapshot(string IncidentId, string Summary, int MinutesOpen);

public record IncidentTriage(string Severity, string RecommendedAction, int ConfidencePercent);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IncidentTriage))]
[JsonSerializable(typeof(IncidentSnapshot))]
internal partial class SmokeJsonContext : JsonSerializerContext;

public static class Program
{
    public static int Main()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = SmokeJsonContext.Default
        };

        var ai = new LlmCalloutOptions { JsonSerializerOptions = options };
        ai.RegisterResponseType<IncidentTriage>();

        // The real production type, not a reimplementation of it: LlmCalloutExecutor delegates every
        // trim-sensitive step to this, and cannot itself be constructed here because it needs an
        // IWolverineRuntime, whose bootstrapping is not AOT-clean on its own account.
        var binder = new LlmResponseBinder(ai);

        // Authoring a callout: the trim-clean context overload, serializing through source
        // generated type info rather than through reflection over the object's shape.
        var callout = LlmCallout.Ask<IncidentTriage>("Classify this incident.")
            .WithContext(new IncidentSnapshot("INC-1", "database is on fire", 12),
                SmokeJsonContext.Default.IncidentSnapshot)
            .Tagged("triage");

        if (!callout.ExpectsResponse<IncidentTriage>()) return Fail("the callout lost its response type");
        if (callout.Context is null || !callout.Context.Contains("INC-1")) return Fail("the context did not serialize");

        // Resolution: identifier off the wire back to a Type, through the registration table.
        if (!binder.TryResolveRegisteredType(callout.ResponseType!, out var responseType))
        {
            return Fail($"'{callout.ResponseType}' did not resolve through the registration table");
        }

        // Schema generation from that Type, off the source generated type info.
        var rendered = binder.SchemaFor(responseType).ToString();
        Console.WriteLine($"schema: {rendered}");

        // An empty schema is how this fails when the resolver cannot see the type: the call
        // succeeds and returns {} rather than throwing, and the model is then handed a
        // constraint that constrains nothing. Assert on a property name it must contain.
        if (!rendered.Contains("recommendedAction"))
        {
            return Fail("the generated schema is missing the response type's properties");
        }

        // Deserialization into that same runtime Type, through the JsonTypeInfo overload.
        const string answer = """{"severity":"high","recommendedAction":"page the on-call","confidencePercent":92}""";
        var value = binder.Deserialize(answer, responseType);

        if (value is not IncidentTriage { Severity: "high", ConfidencePercent: 92 })
        {
            return Fail($"the answer deserialized to '{value}'");
        }

        Console.WriteLine("Wolverine.AI AOT smoke test passed");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"Wolverine.AI AOT smoke test FAILED: {message}");
        return 1;
    }
}
