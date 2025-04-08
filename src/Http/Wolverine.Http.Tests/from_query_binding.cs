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

    private async Task<BigQuery> forQuerystring(string querystring)
    {
        var result = await Host.Scenario(x => x.Get.Url($"/api/bigquery?{querystring}"));
        return result.ReadAsJson<BigQuery>();
    }

    [Fact]
    public async Task resolve_data_on_setters()
    {
        (await forQuerystring("Name=Jones&Number=95")).Name.ShouldBe("Jones");
        (await forQuerystring("Name=Jones&Number=95")).Number.ShouldBe(95);
        (await forQuerystring("Name=Jones&Number=95&Direction=West")).Direction.ShouldBe(Direction.West);
    }

    [Fact]
    public async Task resolve_bool_on_setter()
    {
        (await forQuerystring("Name=Jones&Number=95")).Flag.ShouldBeFalse();
        (await forQuerystring("Name=Jones&Number=95&Flag=true")).Flag.ShouldBeTrue();
    }

    [Fact]
    public async Task nullable_number_on_setter()
    {
        (await forQuerystring("Name=Jones&Number=95")).NullableNumber.ShouldBeNull();
        (await forQuerystring("Name=Jones&Number=95&NullableNumber=33")).NullableNumber.ShouldBe(33);
    }
    
    [Fact]
    public async Task nullable_bool_on_setter()
    {
        (await forQuerystring("Name=Jones&Number=95")).NullableFlag.ShouldBeNull();
        (await forQuerystring("Name=Jones&Number=95&NullableFlag=true")).NullableFlag.Value.ShouldBeTrue();
        (await forQuerystring("Name=Jones&Number=95&NullableFlag=false")).NullableFlag.Value.ShouldBeFalse();
    }
    
    [Fact]
    public async Task nullable_enum_on_setter()
    {
        (await forQuerystring("Name=Jones&Number=95")).NullableDirection.ShouldBeNull();
        (await forQuerystring("Name=Jones&Number=95&NullableDirection=East")).NullableDirection.Value.ShouldBe(Direction.East);
    }

    [Fact]
    public async Task string_array_as_property()
    {
        (await forQuerystring("Name=Jones&Number=95")).Values.ShouldBeEmpty();
    
        var result = await Scenario(x =>
        {
            x.Get
                .Url("/api/bigquery")
                .QueryString("Values", "one")
                .QueryString("Values", "two")
                .QueryString("Values", "three");
        });
    
        var query = result.ReadAsJson<BigQuery>();
        query.Values.ShouldBe(["one", "two", "three"]);
    }
    
    [Fact]
    public async Task int_array_as_property()
    {
        (await forQuerystring("Name=Jones&Number=95")).Numbers.ShouldBeNull();
    
        var result = await Scenario(x =>
        {
            x.Get
                .Url("/api/bigquery")
                .QueryString("Numbers", "1")
                .QueryString("Numbers", "3")
                .QueryString("Numbers", "4");
        });
    
        var query = result.ReadAsJson<BigQuery>();
        query.Numbers.ShouldBe([1, 3, 4]);
    }
}