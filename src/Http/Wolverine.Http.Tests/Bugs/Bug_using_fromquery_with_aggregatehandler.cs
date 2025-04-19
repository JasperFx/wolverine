using System.Diagnostics;
using Alba;
using IntegrationTests;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public static class GetUsingFromQueryAndAggregateHandlerEndpoint
{
    [WolverineGet("getusingfromqueryandaggregatehandler")]
    [AggregateHandler]
    public static (UpdatedAggregate, Events) Get([FromQuery]FromQueryAggregateHandlerInput input, FromQueryAggregateHandlerAggregate testAggregate)
    {
        return (new UpdatedAggregate(), [new FromQueryAggregateHandlerEvent(input.Id, input.Something)]);
    }
}
public record FromQueryAggregateHandlerInput(Guid Id, string Something);
public record FromQueryAggregateHandlerEvent(Guid Id, string Something);


    public class FromQueryAggregateHandlerAggregate
    {
        public Guid Id { get; set; }
        public string Something{get;set;}

        public void Apply(FromQueryAggregateHandlerEvent e){
            this.Id = e.Id;
            this.Something = e.Something;
        }

    }

public class Bug_using_fromquery_with_aggregatehandler
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
            opts.DatabaseSchemaName = "fromquery_aggregatehandler";
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.ApplicationAssembly = typeof(GetUsingFromQueryAndAggregateHandlerEndpoint).Assembly;
        });

        builder.Services.AddWolverineHttp();


        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        await using var session = host.DocumentStore().LightweightSession();
        var aggregateId = Guid.NewGuid();
        session.Events.StartStream(aggregateId, new FromQueryAggregateHandlerEvent(Guid.NewGuid(), "Something1"));
        await session.SaveChangesAsync();

        var body = await host.Scenario(x => x.Get.Url("/getusingfromqueryandaggregatehandler?id=" + aggregateId +"&something=Something2"));

        body.ReadAsJson<FromQueryAggregateHandlerAggregate>().Something.ShouldBe("Something2");

    }
}

