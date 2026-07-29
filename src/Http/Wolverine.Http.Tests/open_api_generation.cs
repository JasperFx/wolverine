using System.Diagnostics;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Shouldly;
using WolverineWebApi;
using WolverineWebApi.TestSupport;

namespace Wolverine.Http.Tests;

/// <summary>
/// The attribute-driven OpenAPI *shape* harness on the Swashbuckle stack: any endpoint in WolverineWebApi
/// annotated with an <see cref="OpenApiExpectationAttribute"/> ([ExpectParameter], [ExpectParameterCount],
/// [ExpectRequestBody], [ExpectNoRequestBody], [ExpectProduces], [ExpectStatusCodes], [ExpectMatch]) is fed
/// through <see cref="verify_open_api_expectations"/>, which resolves the *rendered* OpenAPI document and
/// validates the expectations against the real operation.
///
/// TO ADD A SHAPE: annotate an endpoint method in WolverineWebApi with the Expect* attributes describing
/// what the operation must look like. No test code required — this theory picks it up automatically.
///
/// The Microsoft.AspNetCore.OpenApi half of the shape coverage lives in openapi_shape_tests.cs.
/// </summary>
public class open_api_generation : IntegrationContext
{
    public open_api_generation(AppFixture fixture) : base(fixture)
    {
    }

    public static object[][] Chains()
    {
        // [MemberData] is evaluated during test DISCOVERY. Under xUnit v3 the test project is an
        // executable whose stdout IS the runner's JSON protocol channel, and discovery happens
        // outside the per-test console capture. Booting a Wolverine host here writes its startup
        // logging ("Found N Wolverine HTTP endpoints") straight into that channel and kills the
        // entire assembly with "Test process did not return valid JSON" -- no tests run at all.
        //
        // Mute stdout across the bootstrap. The console logger captures Console.Out when it is
        // constructed, which happens inside this block, so it picks up the null writer.
        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);

        try
        {
            var fixture = new AppFixture();
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            fixture.InitializeAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

            var chains = fixture
                .Host!
                .Services
                .GetRequiredService<WolverineHttpOptions>()
                .Endpoints!
                .Chains
                .Where(x => x.Method.Method.HasAttribute<OpenApiExpectationAttribute>())
                .Select(x => new object[]{x}).ToArray();

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            fixture.DisposeAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

            return chains;
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Theory]
    [MemberData(nameof(Chains))]
    public void verify_open_api_expectations(HttpChain chain)
    {
        var opType = Enum.Parse<OperationType>(chain.HttpMethods.Single(), true);

        var (item, op) = FindOpenApiDocument(opType, chain.RoutePattern!.RawText!);

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
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        Debug.WriteLine(endpoint);

        var description = chain.CreateApiDescription("get");

        Debug.WriteLine(description);
    }
}