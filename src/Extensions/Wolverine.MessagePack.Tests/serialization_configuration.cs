#pragma warning disable IDE0058
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.MessagePack.Internal;
using Wolverine.Runtime;
using Wolverine.Util;
using Xunit;

namespace Wolverine.MessagePack.Tests;

public class serialization_configuration
{
    [Fact]
    public async Task can_override_the_default_app_wide()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseMessagePackSerialization();
            opts.PublishAllMessages().To("stub://one");
            opts.ListenForMessagesFrom("stub://two");
        }).StartAsync();

        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())
            ?.DefaultSerializer.ShouldBeOfType<MessagePackMessageSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())
            ?.DefaultSerializer.ShouldBeOfType<MessagePackMessageSerializer>();
    }

    [Fact]
    public async Task can_override_the_serialization_on_just_one_endpoint()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.PublishAllMessages().To("stub://one").UseMessagePackSerialization();
            opts.ListenForMessagesFrom("stub://two").UseMessagePackSerialization();
            opts.ListenForMessagesFrom("stub://three");
        }).StartAsync();

        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())
            ?.DefaultSerializer.ShouldBeOfType<MessagePackMessageSerializer>();

        root.Endpoints.EndpointFor("stub://two".ToUri())
            ?.DefaultSerializer.ShouldBeOfType<MessagePackMessageSerializer>();

        root.Endpoints.EndpointFor("stub://three".ToUri())
            ?.DefaultSerializer.ShouldNotBeOfType<MessagePackMessageSerializer>();
    }
}
