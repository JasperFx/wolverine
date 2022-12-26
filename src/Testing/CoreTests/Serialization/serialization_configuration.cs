using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;
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

        root.Endpoints.EndpointFor("stub://one".ToUri())
            .DefaultSerializer.ShouldBeOfType<NewtonsoftSerializer>();
        root.Endpoints.EndpointFor("stub://two".ToUri())
            .DefaultSerializer.ShouldBeOfType<NewtonsoftSerializer>();
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
        root.Endpoints.EndpointFor("stub://one".ToUri())
            .DefaultSerializer.ShouldBeOfType<NewtonsoftSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())
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
        root.Endpoints.EndpointFor("stub://one".ToUri())
            .TryFindSerializer("text/foo")
            .ShouldBeOfType<FooSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())
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
        root.Endpoints.EndpointFor("stub://one".ToUri())
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
        root.Endpoints.EndpointFor("stub://one".ToUri())
            .DefaultSerializer.ShouldBeOfType<NewtonsoftSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())
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
        root.Endpoints.EndpointFor("stub://one".ToUri())
            .DefaultSerializer.ShouldBeOfType<NewtonsoftSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())
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
        root.Endpoints.EndpointFor("stub://one".ToUri())
            .DefaultSerializer.ShouldBe(fooSerializer);

        root.Endpoints.EndpointFor("stub://two".ToUri())
            .DefaultSerializer.ShouldBe(fooSerializer);
    }

    public class FooSerializer : IMessageSerializer
    {
        public string? ContentType { get; } = "text/foo";

        public object? ReadFromData(byte[]? data)
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