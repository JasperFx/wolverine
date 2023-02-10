using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class respecting_fromservices_attribute : IntegrationContext
{
    public respecting_fromservices_attribute(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task concrete_type_decorated_with_fromservices_is_sourced_by_container()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Scenario(x =>
        {
            x.Post.Url("/fromservices");
        });
        
        recorder.Actions.Single().ShouldBe("Called AttributesEndpoints.Post()");
    }
    
    [Fact]
    public async Task concrete_type_decorated_with_NotBody_is_sourced_by_container()
    {
        var recorder = Host.Services.GetRequiredService<Recorder>();
        recorder.Actions.Clear();

        await Scenario(x =>
        {
            x.Post.Url("/notbody");
        });
        
        recorder.Actions.Single().ShouldBe("Called AttributesEndpoints.Post()");
    }
}