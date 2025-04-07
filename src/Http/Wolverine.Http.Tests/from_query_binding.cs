using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class from_query_binding : IntegrationContext
{
    public from_query_binding(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task get_string_as_parameter()
    {
        var result = await Host.Scenario(x => x.Get.Url("/api/fromquery1?Name=Mahomes"));
        result.ReadAsJson<Query1>().Name.ShouldBe("Mahomes");
        
        var result2 = await Host.Scenario(x => x.Get.Url("/api/fromquery1"));
        result2.ReadAsJson<Query1>().Name.ShouldBeNull();
    }

    [Fact]
    public async Task get_number_as_parameter()
    {
        var result = await Host.Scenario(x => x.Get.Url("/api/fromquery2?Number=15"));
        result.ReadAsJson<Query2>().Number.ShouldBe(15);
        
        var result2 = await Host.Scenario(x => x.Get.Url("/api/fromquery2"));
        result2.ReadAsJson<Query2>().Number.ShouldBe(0);
    }

    [Fact]
    public async Task get_guid_as_parameter()
    {
        var id = Guid.NewGuid();
        var result = await Host.Scenario(x => x.Get.Url("/api/fromquery3?Id=" + id));
        result.ReadAsJson<Query3>().Id.ShouldBe(id);
        
        var result2 = await Host.Scenario(x => x.Get.Url("/api/fromquery3"));
        result2.ReadAsJson<Query3>().Id.ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task get_multiple_parameters()
    {
        var result = await Host.Scenario(x => x.Get.Url("/api/fromquery4?Name=Kelce&Number=87&Direction=North"));
        var query = result.ReadAsJson<Query4>();
        query.Name.ShouldBe("Kelce");
        query.Number.ShouldBe(87);
        query.Direction.ShouldBe(Direction.North);
    }
}