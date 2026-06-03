using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Wolverine.Newtonsoft;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

public class serialization_configuration
{
    [Fact]
    public async Task by_default_every_endpoint_has_json_serializer_with_default_settings()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.PublishAllMessages().To("stub://one");
            opts.PublishAllMessages().To("stub://two");
        }).StartAsync();

        var root = host.Services.GetRequiredService<IWolverineRuntime>();

        root.Endpoints.EndpointFor("stub://one".ToUri())!
            .DefaultSerializer.ShouldBeOfType<SystemTextJsonSerializer>();
        root.Endpoints.EndpointFor("stub://two".ToUri())!
            .DefaultSerializer.ShouldBeOfType<SystemTextJsonSerializer>();
    }

    [Fact]
    public async Task can_override_the_json_serialization_on_subscriber()
    {
        var customSettings = new JsonSerializerSettings();

        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.PublishAllMessages().To("stub://one");

            opts.PublishAllMessages().To("stub://two")
                .CustomNewtonsoftJsonSerialization(customSettings);
        }).StartAsync();

        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())!
            .DefaultSerializer.ShouldBeOfType<SystemTextJsonSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())!
            .DefaultSerializer.ShouldBeOfType<NewtonsoftSerializer>()
            .Settings.ShouldBeSameAs(customSettings);
    }

    [Fact]
    public async Task can_find_other_serializer_from_parent()
    {
        var customSettings = new JsonSerializerSettings();

        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.AddSerializer(new FooSerializer());
            opts.PublishAllMessages().To("stub://one");

            opts.ListenForMessagesFrom("stub://two")
                .CustomNewtonsoftJsonSerialization(customSettings);
        }).StartAsync();

        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())!
            .TryFindSerializer("text/foo")
            .ShouldBeOfType<FooSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())!
            .TryFindSerializer("text/foo")
            .ShouldBeOfType<FooSerializer>();
    }

    [Fact]
    public async Task can_override_the_default_serializer_on_sender()
    {
        var customSettings = new JsonSerializerSettings();
        var fooSerializer = new FooSerializer();

        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.PublishAllMessages().To("stub://one")
                .DefaultSerializer(fooSerializer);

            opts.ListenForMessagesFrom("stub://two")
                .CustomNewtonsoftJsonSerialization(customSettings);
        }).StartAsync();

        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())!
            .DefaultSerializer.ShouldBeSameAs(fooSerializer);
    }

    [Fact]
    public async Task can_override_the_json_serialization_on_listener()
    {
        var customSettings = new JsonSerializerSettings();

        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.PublishAllMessages().To("stub://one");

            opts.ListenForMessagesFrom("stub://two")
                .CustomNewtonsoftJsonSerialization(customSettings);
        }).StartAsync();

        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())!
            .DefaultSerializer.ShouldBeOfType<SystemTextJsonSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())!
            .DefaultSerializer.ShouldBeOfType<NewtonsoftSerializer>()
            .Settings.ShouldBeSameAs(customSettings);
    }

    [Fact]
    public async Task can_override_the_default_serialization_on_listener()
    {
        var fooSerializer = new FooSerializer();

        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.PublishAllMessages().To("stub://one");

            opts.ListenForMessagesFrom("stub://two")
                .DefaultSerializer(fooSerializer);
        }).StartAsync();

        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())!
            .DefaultSerializer.ShouldBeOfType<SystemTextJsonSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())!
            .DefaultSerializer.ShouldBeSameAs(fooSerializer);
    }

    [Fact]
    public async Task can_override_the_default_app_wide()
    {
        var fooSerializer = new FooSerializer();

        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.DefaultSerializer = fooSerializer;
            opts.PublishAllMessages().To("stub://one");

            opts.ListenForMessagesFrom("stub://two");
        }).StartAsync();

        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())!
            .DefaultSerializer.ShouldBe(fooSerializer);

        root.Endpoints.EndpointFor("stub://two".ToUri())!
            .DefaultSerializer.ShouldBe(fooSerializer);
    }

    [Fact]
    public async Task custom_serializer_on_sender_is_used_to_produce_outgoing_envelopes()
    {
        // Regression for CritterWatch#261 (H1): a custom IMessageSerializer attached at the
        // rule/endpoint level via .DefaultSerializer(...) must be the serializer that actually
        // produces the outgoing envelope — and therefore the wire bytes — rather than being
        // silently replaced by the global System.Text.Json default at Compile() time.
        var custom = new RecordingSerializer();

        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.PublishMessage<CustomSerializedMessage>().To("stub://custom")
                .DefaultSerializer(custom);

            // Sibling sender with no override — must stay on the global STJ default.
            opts.PublishMessage<CustomSerializedMessage>().To("stub://plain");
        }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var router = runtime.RoutingFor(typeof(CustomSerializedMessage));

        var envelopes = router.RouteForSend(new CustomSerializedMessage("hi"), null);
        envelopes.Length.ShouldBe(2);

        // The route to the overridden endpoint carries the custom serializer + its content type.
        var customEnvelope = envelopes.Single(e => ReferenceEquals(e.Serializer, custom));
        customEnvelope.ContentType.ShouldBe("text/recording");

        // ...and the wire payload is produced by that custom serializer, exactly as the sending
        // agent does (envelope.Serializer.Write(envelope)).
        customEnvelope.Serializer!.Write(customEnvelope).ShouldBe(RecordingSerializer.Sentinel);
        custom.WriteCount.ShouldBeGreaterThan(0);

        // Isolation: the un-overridden sibling endpoint still uses the global System.Text.Json default.
        envelopes.ShouldContain(e => e.Serializer is SystemTextJsonSerializer);
    }

    public record CustomSerializedMessage(string Name);

    // A custom serializer with a working Write so we can prove the *actual wire bytes* come from it.
    public class RecordingSerializer : IMessageSerializer
    {
        public static readonly byte[] Sentinel = [9, 8, 7, 6];
        public int WriteCount { get; private set; }

        public string ContentType => "text/recording";

        public byte[] WriteMessage(object message)
        {
            WriteCount++;
            return Sentinel;
        }

        public byte[] Write(Envelope envelope)
        {
            WriteCount++;
            return Sentinel;
        }

        public object ReadFromData(byte[] data) => throw new NotImplementedException();

        public object ReadFromData(Type messageType, Envelope envelope) => throw new NotImplementedException();
    }

    public class FooSerializer : IMessageSerializer
    {
        public string ContentType => "text/foo";

        public object ReadFromData(byte[] data)
        {
            throw new NotImplementedException();
        }

        public byte[] WriteMessage(object message)
        {
            throw new NotImplementedException();
        }

        public byte[] Write(Envelope envelope)
        {
            throw new NotImplementedException();
        }

        public object ReadFromData(Type messageType, Envelope envelope)
        {
            throw new NotImplementedException();
        }
    }
}