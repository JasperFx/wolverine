using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

public record ContextDeclaredMessage(Guid Id, string Name, int Count);

public record ContextUndeclaredMessage(string Name);

[JsonSerializable(typeof(ContextDeclaredMessage))]
internal partial class Gh5148JsonContext : JsonSerializerContext;

// GH-5148. UseSystemTextJsonForSerialization(JsonSerializerContext) — the source generated, AOT-safe path
// for the default serializer.
public class system_text_json_with_a_serializer_context_5148
{
    private static SystemTextJsonSerializer defaultSerializerOf(WolverineOptions options)
    {
        return options.DefaultSerializer.ShouldBeOfType<SystemTextJsonSerializer>();
    }

    [Fact]
    public void the_context_becomes_the_type_info_resolver_of_the_default_serializer()
    {
        var options = new WolverineOptions();
        options.UseSystemTextJsonForSerialization(Gh5148JsonContext.Default);

        defaultSerializerOf(options).Options.TypeInfoResolver.ShouldBeSameAs(Gh5148JsonContext.Default);
    }

    [Fact]
    public void wolverine_defaults_still_apply_and_the_configuration_runs_after_the_context()
    {
        var options = new WolverineOptions();
        options.UseSystemTextJsonForSerialization(Gh5148JsonContext.Default, o => o.WriteIndented = true);

        var stj = defaultSerializerOf(options).Options;
        stj.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.CamelCase);
        stj.WriteIndented.ShouldBeTrue();
    }

    [Fact]
    public void round_trips_a_declared_message_through_the_context()
    {
        var options = new WolverineOptions();
        options.UseSystemTextJsonForSerialization(Gh5148JsonContext.Default);
        var serializer = defaultSerializerOf(options);

        var message = new ContextDeclaredMessage(Guid.NewGuid(), "Wolverine", 3);
        var envelope = new Envelope(message);
        envelope.Data = serializer.Write(envelope);

        System.Text.Encoding.UTF8.GetString(envelope.Data).ShouldContain("\"name\":\"Wolverine\"");

        serializer.ReadFromData(typeof(ContextDeclaredMessage), envelope).ShouldBe(message);
    }

    [Fact]
    public void a_message_type_the_context_does_not_declare_is_refused()
    {
        var options = new WolverineOptions();
        options.UseSystemTextJsonForSerialization(Gh5148JsonContext.Default);
        var serializer = defaultSerializerOf(options);

        Should.Throw<NotSupportedException>(() => serializer.WriteMessage(new ContextUndeclaredMessage("x")));
    }

    [Fact]
    public void can_chain_a_reflection_fallback_for_undeclared_types()
    {
        var options = new WolverineOptions();
        options.UseSystemTextJsonForSerialization(Gh5148JsonContext.Default,
            o => o.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver()));
        var serializer = defaultSerializerOf(options);

        var envelope = new Envelope(new ContextUndeclaredMessage("fallback"));
        envelope.Data = serializer.Write(envelope);

        serializer.ReadFromData(typeof(ContextUndeclaredMessage), envelope)
            .ShouldBe(new ContextUndeclaredMessage("fallback"));
    }

    [Fact]
    public void the_default_reflection_path_is_unchanged()
    {
        var options = new WolverineOptions();
        var serializer = defaultSerializerOf(options);
        serializer.Options.TypeInfoResolver.ShouldBeNull();

        var message = new ContextUndeclaredMessage("reflection");
        var envelope = new Envelope(message);
        envelope.Data = serializer.Write(envelope);

        serializer.ReadFromData(typeof(ContextUndeclaredMessage), envelope).ShouldBe(message);
    }
}
