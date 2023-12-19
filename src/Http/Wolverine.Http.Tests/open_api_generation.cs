using System.Diagnostics;
using System.Net;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Shouldly;
using Swashbuckle.AspNetCore.Swagger;
using TestingSupport;
using WolverineWebApi;
using WolverineWebApi.TestSupport;

namespace Wolverine.Http.Tests;

public class open_api_generation : IntegrationContext
{
    public open_api_generation(AppFixture fixture) : base(fixture)
    {
    }
    
    

    public static object[][] Chains()
    {
        var fixture = new AppFixture();
        fixture.InitializeAsync().GetAwaiter().GetResult();
        
        var chains = fixture
            .Host
            .Services
            .GetRequiredService<WolverineHttpOptions>()
            .Endpoints!
            .Chains
            .Where(x => x.Method.Method.HasAttribute<OpenApiExpectationAttribute>())
            .Select(x => new object[]{x}).ToArray();
        
        fixture.DisposeAsync().GetAwaiter().GetResult();

        return chains;
    }
    
    [Theory]
    [MemberData(nameof(Chains))]
    public void verify_open_api_expectations(HttpChain chain)
    {
        var opType = Enum.Parse<OperationType>(chain.HttpMethods.Single(), true);

        var (item, op) = FindOpenApiDocument(opType, chain.RoutePattern.RawText);

        item.ShouldNotBeNull();
        op.ShouldNotBeNull();

        var expectations = chain.Method.Method.GetAllAttributes<OpenApiExpectationAttribute>();

        foreach (var expectation in expectations)
        {
            expectation.Validate(item, op, this);
        }
    }



}

public class try_build_chain
{
    [Fact]
    public void create_chain()
    {
        var chain = HttpChain.ChainFor<OpenApiEndpoints>(x => x.GetJson());
        var endpoint = chain.BuildEndpoint();
        
        Debug.WriteLine(endpoint);

        var description = chain.CreateApiDescription("get");
        
        Debug.WriteLine(description);
    }
}

