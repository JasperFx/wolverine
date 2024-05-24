using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Wolverine.Attributes;
using WolverineWebApi;

namespace Wolverine.Http.Tests
{
    public class acceptresponse_statuscode
    {
        [Fact]
        public async Task should_be_202_given_AcceptResponse()
        {
            var builder = WebApplication.CreateBuilder();

            builder.Host.UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IgnoreAssembly(typeof(OpenApiEndpoints).Assembly);
                opts.Discovery.IncludeAssembly(GetType().Assembly);

                opts.Services.AddMarten(Servers.PostgresConnectionString);
            });

            builder.Services.AddWolverineHttp();

            await using var host = await AlbaHost.For(builder, app =>
            {
                app.MapWolverineEndpoints();
            });

            await host.Scenario(x =>
            {
                x.Post.Json(new AcceptResponseRequest()).ToUrl("/api/blah-blah");
                x.StatusCodeShouldBe(202);
            });
        }
    }

    public record AcceptResponseRequest;

    public class AcceptResponseHandler
    {
        [WolverineHandler]
        public static AcceptResponse Handle(AcceptResponseRequest _)
        {
            return new AcceptResponse("/api/blah-blah");
        }
    }

    public class AcceptResponseEndpoint
    {
        [WolverinePost("/api/blah-blah")]
        public static AcceptResponse Post(AcceptResponseRequest request)
        {
            return new AcceptResponse("/api/blah-blah");
        }
    }
}
