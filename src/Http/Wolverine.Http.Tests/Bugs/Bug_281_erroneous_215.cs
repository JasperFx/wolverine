using System.Net.Http.Json;
using JasperFx.Core.Reflection;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_281_erroneous_215 : IntegrationContext
{
    public Bug_281_erroneous_215(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task should_get_a_204_happy_as_you_please()
    {
        var signUpRequest = new SignUpRequest("test@email.com", "P4$$w0rd123123!");

        await Scenario(x =>
        {
            x.Post.Json(signUpRequest).ToUrl("/users/sign-up");
            x.StatusCodeShouldBe(204);
        });

        var client = Host.Server.CreateClient();
        var response = await client.PostAsJsonAsync("/users/sign-up", signUpRequest);
        response.StatusCode.As<int>().ShouldBe(204);
    }
}