using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.MemoryPack.Internal;
using Wolverine.Runtime;
using Xunit;
using Wolverine.Util;

namespace Wolverine.MemoryPack.Tests;

public class serialization_configuration
{
    [Fact]
    public async Task can_override_the_default_app_wide()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseMemoryPackSerialization();
            opts.PublishAllMessages().To("stub://one");
            opts.ListenForMessagesFrom("stub://two");
        }).StartAsync();
    
        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())
            ?.DefaultSerializer.ShouldBeOfType<Internal.MemoryPackMessageSerializer>();
    
        root.Endpoints.EndpointFor("stub://two".ToUri())
            ?.DefaultSerializer.ShouldBeOfType<Internal.MemoryPackMessageSerializer>();
    }
    
    
    [Fact]
    public async Task can_override_the_serialization_on_just_one_endpoint()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.PublishAllMessages().To("stub://one").UseMemoryPackSerialization();
            opts.ListenForMessagesFrom("stub://two").UseMemoryPackSerialization();
            opts.ListenForMessagesFrom("stub://three");
        }).StartAsync();
    
        var root = host.Services.GetRequiredService<IWolverineRuntime>();
        root.Endpoints.EndpointFor("stub://one".ToUri())
            ?.DefaultSerializer.ShouldBeOfType<Internal.MemoryPackMessageSerializer>();
    
        root.Endpoints.EndpointFor("stub://two".ToUri())
            ?.DefaultSerializer.ShouldBeOfType<Internal.MemoryPackMessageSerializer>();
        
        root.Endpoints.EndpointFor("stub://three".ToUri())
            ?.DefaultSerializer.ShouldNotBeOfType<MemoryPackMessageSerializer>();
    }
}