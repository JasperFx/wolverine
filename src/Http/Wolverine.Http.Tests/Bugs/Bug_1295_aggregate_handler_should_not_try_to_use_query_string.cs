using System.Diagnostics;
using Alba;
using IntegrationTests;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_1295_aggregate_handler_should_not_try_to_use_query_string
{
    [Fact]
    public async Task run_end_to_end()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DisableNpgsqlLogging = true;
            opts.Events.StreamIdentity = StreamIdentity.AsString;
            opts.DatabaseSchemaName = "gh1295";
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.ApplicationAssembly = GetType().Assembly;
        });

        builder.Services.AddWolverineHttp();

        builder.Services.AddWolverineHttp();

        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        await using var session = host.DocumentStore().LightweightSession();
        var streamKey = Guid.NewGuid().ToString();
        session.Events.StartStream(streamKey, new TestEvent());
        await session.SaveChangesAsync();

        await host.Scenario(x =>
        {
            x.Post.Json(new TestInput(streamKey)).ToUrl("/atest");
            x.StatusCodeShouldBe(204);
        });

    }
}

public class AggregateHandlerEndpoint
{
    [WolverinePost("atest")]
    [AggregateHandler, EmptyResponse]
    public static TestEvent DoSomething1(TestInput input, TestAggregate testAggregate)
    {
        return new TestEvent();
    }
    
    [WolverinePost("atest2/{id}")]
    [EmptyResponse]
    public static TestEvent DoSomething2(TestInput input, [Aggregate]TestAggregate testAggregate)
    {
        return new TestEvent();
    }
    
    [WolverinePost("atest3")]
    [EmptyResponse]
    public static TestEvent DoSomething3(TestInput input, [ReadAggregate]TestAggregate testAggregate)
    {
        return new TestEvent();
    }
}

public record TestEvent;

public record TestInput(string Id);

public class TestAggregate
{
    public string Id { get; set; }

    public void Apply(TestEvent _) => Debug.WriteLine("okay");

}