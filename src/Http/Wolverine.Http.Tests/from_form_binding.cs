using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class from_form_binding : IntegrationContext
{
    public from_form_binding(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task get_string_as_parameter()
    {
        var result = await Host.Scenario(x => x
            .Post.FormData(new Dictionary<string,string>(){
                {"name", "Mahomes"}
            })
            .ToUrl("/api/fromform1"));
        result.ReadAsJson<Query1>().Name.ShouldBe("Mahomes");
        
        var result2 = await Host.Scenario(x => x.Post.FormData([]).ToUrl("/api/fromform1"));
        result2.ReadAsJson<Query1>().Name.ShouldBeNull();
    }

    [Fact]
    public async Task get_number_as_parameter()
    {
        var result = await Host.Scenario(x => x.Post.FormData(new Dictionary<string,string>(){{"number", "15"}}).ToUrl("/api/fromform2"));
        result.ReadAsJson<Query2>().Number.ShouldBe(15);
        
        var result2 = await Host.Scenario(x => x.Post.FormData([]).ToUrl("/api/fromform2"));
        result2.ReadAsJson<Query2>().Number.ShouldBe(0);
    }

    [Fact]
    public async Task get_guid_as_parameter()
    {
        var id = Guid.NewGuid();
        var result = await Host.Scenario(x => x.Post.FormData(new Dictionary<string,string>(){{"Id", id.ToString()}}).ToUrl("/api/fromform3"));
        result.ReadAsJson<Query3>().Id.ShouldBe(id);
        
        var result2 = await Host.Scenario(x => x.Post.FormData([]).ToUrl("/api/fromform3"));
        result2.ReadAsJson<Query3>().Id.ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task get_multiple_parameters()
    {
        var result = await Host.Scenario(x => x.Post.FormData(new Dictionary<string,string>{
            {"name", "Kelce"},
            {"number", "87"},
            {"direction", "north"}
        }).ToUrl("/api/fromform4"));
        var query = result.ReadAsJson<Query4>();
        query.Name.ShouldBe("Kelce");
        query.Number.ShouldBe(87);
        query.Direction.ShouldBe(Direction.North);
    }

    private async Task<BigQuery> forForm(List<KeyValuePair<string,string>> values)
    {
        var result = await Host.Scenario(x => x
            .Post.FormData(values.ToDictionary(x => x.Key, x => x.Value))
            .ToUrl("/api/fromformbigquery"));
        return result.ReadAsJson<BigQuery>();
    }

    [Fact]
    public async Task resolve_data_on_setters()
    {
        var result = await forForm([new("name", "Jones"), new("number", "95"), new("direction", "west")]);
        result.Name.ShouldBe("Jones");
        result.Number.ShouldBe(95);
        result.Direction.ShouldBe(Direction.West);
    }

    [Fact]
    public async Task resolve_bool_on_setter()
    {
        (await forForm([new("name", "Jones"), new("number", "95")])).Flag.ShouldBeFalse();
        (await forForm([new("name", "Jones"), new("number", "95"), new("flag", "true")])).Flag.ShouldBeTrue();
    }

    [Fact]
    public async Task nullable_number_on_setter()
    {
        (await forForm([new("name", "Jones"), new("number", "95")])).NullableNumber.ShouldBeNull();
        (await forForm([new("name", "Jones"), new("number", "95"), new("nullableNumber", "33")])).NullableNumber.ShouldBe(33);
    }
    
    [Fact]
    public async Task nullable_bool_on_setter()
    {
        (await forForm([new("name", "Jones"), new("number", "95")])).NullableFlag.ShouldBeNull();
        (await forForm([new("name", "Jones"), new("number", "95"), new("nullableFlag", "true")])).NullableFlag.Value.ShouldBeTrue();
        (await forForm([new("name", "Jones"), new("number", "95"), new("NullableFlag", "false")])).NullableFlag.Value.ShouldBeFalse();
    }
    
    [Fact]
    public async Task nullable_enum_on_setter()
    {
        (await forForm([new("name", "Jones"), new("number", "95")])).NullableDirection.ShouldBeNull();
        (await forForm([new("name", "Jones"), new("number", "95"), new("nullableDirection", "east")])).NullableDirection.Value.ShouldBe(Direction.East);
    }

    [Fact(Skip = "Alba doesnt support multiple values in form data")]
    public async Task string_array_as_property()
    {
        (await forForm([new("name", "Jones"), new("number", "95")])).Values.ShouldBeEmpty();
        (await forForm([new("name", "Jones"), new("number", "95"), new("values", "one"), new("values", "two"), new("values", "three")])).Values.ShouldBe(["one", "two", "three"]);
    }
    
    [Fact(Skip = "Alba doesnt support multiple values in form data")]
    public async Task int_array_as_property()
    {
        (await forForm([new("name", "Jones"), new("number", "95")])).Numbers.ShouldBeNull();
        (await forForm([new("name", "Jones"), new("number", "95"), new("numbers", "1"), new("Numbers", "3"), new("Numbers", "4")])).Numbers.ShouldBe([1, 3, 4]);
    }

    [Fact]
    public async Task value_with_alias()
    {
        (await forForm([new("name", "Jones"), new("number", "95")])).ValueWithAlias.ShouldBeNull();
        (await forForm([new("name", "Jones"), new("number", "95"), new("aliased", "foo")])).ValueWithAlias.ShouldBe("foo");
        (await forForm([new("name", "Jones"), new("number", "95"), new("Aliased", "foo")])).ValueWithAlias.ShouldBe("foo");
        (await forForm([new("name", "Jones"), new("number", "95"), new("valueWithAlias", "foo")])).ValueWithAlias.ShouldBeNull();
    }
}