using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class asparameters_binding : IntegrationContext
{
    public asparameters_binding(AppFixture fixture) : base(fixture)
    {
    }


    [Fact]
    public async Task fill_all_fields()
    {
        #region sample_using_asparameters_test
        var result = await Host.Scenario(x => x
            .Post
            .FormData(new Dictionary<string,string>(){
                {"EnumFromForm", "east"},
                {"StringFromForm", "string2"},
                {"IntegerFromForm", "2"},
                {"FloatFromForm", "2.2"},
                {"BooleanFromForm", "true"}, 
                {"StringNotUsed", "string3"},
            }).QueryString("EnumFromQuery", "west")
            .QueryString("StringFromQuery", "string1")
            .QueryString("IntegerFromQuery", "1")
            .QueryString("FloatFromQuery", "1.1")
            .QueryString("BooleanFromQuery", "true")
            .QueryString("IntegerNotUsed", "3")
            .ToUrl("/api/asparameters1")
        );
        var response = result.ReadAsJson<AsParametersQuery>();
        response.EnumFromForm.ShouldBe(Direction.East);
        response.StringFromForm.ShouldBe("string2");
        response.IntegerFromForm.ShouldBe(2);
        response.FloatFromForm.ShouldBe(2.2f);
        response.BooleanFromForm.ShouldBeTrue();
        response.EnumFromQuery.ShouldBe(Direction.West);
        response.StringFromQuery.ShouldBe("string1");
        response.IntegerFromQuery.ShouldBe(1);
        response.FloatFromQuery.ShouldBe(1.1f);
        response.BooleanFromQuery.ShouldBeTrue();
        response.EnumNotUsed.ShouldBe(default);
        response.StringNotUsed.ShouldBe(default);
        response.IntegerNotUsed.ShouldBe(default);
        response.FloatNotUsed.ShouldBe(default);
        response.BooleanNotUsed.ShouldBe(default);
        #endregion
    }
    
}