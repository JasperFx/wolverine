using System.Security.Claims;
using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_http_context_elements_in_routes : IntegrationContext
{

    protected async Task assertExecutesWithNoErrors(string url)
    {
        await Scenario(x =>
        {
            x.Get.Url(url);
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task can_use_http_context_itself()
    {
        await assertExecutesWithNoErrors("/http/context");
    }

    [Fact]
    public async Task can_use_http_request()
    {
        await assertExecutesWithNoErrors("/http/request");
    }
    
    [Fact]
    public async Task can_use_http_response()
    {
        await assertExecutesWithNoErrors("/http/response");
    }

    [Fact]
    public async Task use_claims_principal()
    {
        var principal = new ClaimsPrincipal();

        var body = await Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = principal);
            x.Get.Url("/http/principal");
        });
        
        HttpContextEndpoints.User.ShouldBeSameAs(principal);
    }

    [Fact]
    public async Task using_the_trace_identifier()
    {
        var identifier = Guid.NewGuid().ToString();
        
        var body = await Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.TraceIdentifier = identifier);
            x.Get.Url("/http/identifier");
        });
        
        body.ReadAsText().ShouldBe(identifier);
    }

    public using_http_context_elements_in_routes(AppFixture fixture) : base(fixture)
    {
    }
}