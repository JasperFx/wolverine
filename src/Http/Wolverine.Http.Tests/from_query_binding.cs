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
        var result = await Host.Scenario(x => x.Get.Url("/api/fromquery1?name=Mahomes"));
        result.ReadAsJson<Query1>().Name.ShouldBe("Mahomes");
        
        var result2 = await Host.Scenario(x => x.Get.Url("/api/fromquery1"));
        result2.ReadAsJson<Query1>().Name.ShouldBeNull();
    }

    [Fact]
    public async Task get_number_as_parameter()
    {
        var result = await Host.Scenario(x => x.Get.Url("/api/fromquery2?number=15"));
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
        var result = await Host.Scenario(x => x.Get.Url("/api/fromquery4?name=Kelce&number=87&direction=north"));
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
        (await forQuerystring("name=Jones&number=95")).Name.ShouldBe("Jones");
        (await forQuerystring("name=Jones&number=95")).Number.ShouldBe(95);
        (await forQuerystring("name=Jones&number=95&direction=west")).Direction.ShouldBe(Direction.West);
    }

    [Fact]
    public async Task resolve_bool_on_setter()
    {
        (await forQuerystring("name=Jones&number=95")).Flag.ShouldBeFalse();
        (await forQuerystring("name=Jones&number=95&flag=true")).Flag.ShouldBeTrue();
    }

    [Fact]
    public async Task nullable_number_on_setter()
    {
        (await forQuerystring("name=Jones&number=95")).NullableNumber.ShouldBeNull();
        (await forQuerystring("name=Jones&number=95&nullableNumber=33")).NullableNumber.ShouldBe(33);
    }
    
    [Fact]
    public async Task nullable_bool_on_setter()
    {
        (await forQuerystring("name=Jones&number=95")).NullableFlag.ShouldBeNull();
        (await forQuerystring("name=Jones&number=95&nullableFlag=true")).NullableFlag.Value.ShouldBeTrue();
        (await forQuerystring("name=Jones&number=95&NullableFlag=false")).NullableFlag.Value.ShouldBeFalse();
    }
    
    [Fact]
    public async Task nullable_enum_on_setter()
    {
        (await forQuerystring("name=Jones&number=95")).NullableDirection.ShouldBeNull();
        (await forQuerystring("name=Jones&number=95&nullableDirection=east")).NullableDirection.Value.ShouldBe(Direction.East);
    }

    [Fact]
    public async Task string_array_as_property()
    {
        (await forQuerystring("name=Jones&number=95")).Values.ShouldBeEmpty();
    
        var result = await Scenario(x =>
        {
            x.Get
                .Url("/api/bigquery")
                .QueryString("Values", "one")
                .QueryString("Values", "two")
                .QueryString("values", "three");
        });
    
        var query = result.ReadAsJson<BigQuery>();
        query.Values.ShouldBe(["one", "two", "three"]);
    }
    
    [Fact]
    public async Task int_array_as_property()
    {
        (await forQuerystring("name=Jones&number=95")).Numbers.ShouldBeNull();
    
        var result = await Scenario(x =>
        {
            x.Get
                .Url("/api/bigquery")
                .QueryString("numbers", "1")
                .QueryString("Numbers", "3")
                .QueryString("Numbers", "4");
        });
    
        var query = result.ReadAsJson<BigQuery>();
        query.Numbers.ShouldBe([1, 3, 4]);
    }
}