using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.MultiTenancy;

public class using_tenant_id_in_query_string
{
    [Fact]
    public async Task use_tenant_id_as_querystring_string()
    {
        var builder = WebApplication.CreateBuilder([]);
        
        // config
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "myapp";
        }).IntegrateWithWolverine().UseLightweightSessions();

        
        builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(GetType().Assembly));

        builder.Services.AddWolverineHttp();

        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.TenantId.IsQueryStringValue("tenantId");
            });
        });

        var result = await host.Scenario(x =>
        {
            x.Post.Json(new CreateIssue("One")).ToUrl("/issues/create").QueryString("tenantId", "foo");
        });

        var response = result.ReadAsJson<IssueResponse>();
        response.Name.ShouldBe("One");
    }
}

public record IssueResponse(string Name, Guid Id);

public record CreateIssue(string Name);

public static class CreateIssueEndpoint
{
    [WolverinePost("/issues/create")]
    public static IssueResponse Handle(
        CreateIssue command,
        [FromQuery] string tenantId) // <- will fail when code is generated
    {
        return (
            new IssueResponse(command.Name, Guid.NewGuid())
        );
    }
}

