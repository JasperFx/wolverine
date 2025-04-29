using System.ComponentModel.Design;
using Alba;
using Internal.Generated.WolverineHandlers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class asparameters_binding : IntegrationContext
{
    public asparameters_binding(AppFixture fixture) : base(fixture)
    {
    }
    
    /*
     * TODOs
     * Bind the body, and when you do that, set the request type
     * Bind a service
     * Bind a route argument
     */


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
    
    [Fact]
    public async Task headers_miss()
    {
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
        response.StringHeader.ShouldBeNull();
        response.NumberHeader.ShouldBe(5);
        response.NullableHeader.ShouldBeNull();
        
    }
    
    [Fact]
    public async Task headers_hit()
    {
        var result = await Host.Scenario(x =>
            {
                x.WithRequestHeader("x-string", "Red");
                x.WithRequestHeader("x-number", "303");
                x.WithRequestHeader("x-nullable-number", "13");
                
                x
                    .Post
                    .FormData(new Dictionary<string, string>()
                    {
                        { "EnumFromForm", "east" },
                        { "StringFromForm", "string2" },
                        { "IntegerFromForm", "2" },
                        { "FloatFromForm", "2.2" },
                        { "BooleanFromForm", "true" },
                        { "StringNotUsed", "string3" },
                    }).QueryString("EnumFromQuery", "west")
                    .QueryString("StringFromQuery", "string1")
                    .QueryString("IntegerFromQuery", "1")
                    .QueryString("FloatFromQuery", "1.1")
                    .QueryString("BooleanFromQuery", "true")
                    .QueryString("IntegerNotUsed", "3")

                    .ToUrl("/api/asparameters1");
            }
        );
        var response = result.ReadAsJson<AsParametersQuery>();
        response.StringHeader.ShouldBe("Red");
        response.NumberHeader.ShouldBe(303);
        response.NullableHeader.ShouldBe(13);
        
    }

    [Fact]
    public async Task post_body_services_and_route_arguments()
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new AsParameterBody { Name = "Jeremy", Direction = Direction.East, Distance = 133 })
                .ToUrl("/asp2/croaker/42");
            
                    // x.Post.Url("/asp2/croaker/42");
            
        });

        var response = result.ReadAsJson<AsParametersQuery2>();
        
        // Routes
        response.Id.ShouldBe("croaker");
        response.Number.ShouldBe(42);
        
        // Body
        
        // First check this for OpenAPI generation
        // var options = Host.Services.GetRequiredService<WolverineHttpOptions>();
        // var chain = options.Endpoints.ChainFor("POST", "/asp2/{id}/{number}");
        // chain.RequestType.ShouldBe(typeof(AsParameterBody));
        //
        // response.Body.Name.ShouldBe("Jeremy");
        // response.Body.Direction.ShouldBe(Direction.East);
        // response.Body.Distance.ShouldBe(133);

    }
}