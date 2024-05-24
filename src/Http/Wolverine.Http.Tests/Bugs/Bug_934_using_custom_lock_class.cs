using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_934_using_custom_lock_class
{
    [Fact]
    public async Task can_use_lock()
    {
        var builder = WebApplication.CreateBuilder([]);
        
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();
        
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.ApplicationAssembly = GetType().Assembly;
        });
        
        builder.Services.AddWolverineHttp();
        
        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        await host.Scenario(x =>
        {
            x.Put.Url("/lock/one");
            x.StatusCodeShouldBe(202);
        });
    }
}

public record Lock {
    public required string Id;
}

public record ReservedKeywordBugResponse(string Id)
    : AcceptResponse($"/lock/{Id}");

public static class ReservedKeywordBugEndpoint
{
    public static Lock Load(
        string id
    )
    {
        
        return new Lock { Id = id };
    }

    [WolverinePut("/lock/{lockId}")]
    [ProducesResponseType(typeof(string), 400)]
    public static ReservedKeywordBugResponse Handle(
        string lockId
    )
    {
        var response = new ReservedKeywordBugResponse(lockId);

        return response;
    }
}