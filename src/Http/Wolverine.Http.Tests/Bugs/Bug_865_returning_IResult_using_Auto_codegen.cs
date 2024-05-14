using Alba;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Marten;
using Marten.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Wolverine.ErrorHandling;
using Wolverine.FluentValidation;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_865_returning_IResult_using_Auto_codegen
{
    [Fact]
    public async Task codegen_with_auto()
    {
        var builder = WebApplication.CreateBuilder([]);
        
// config
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "myapp";

            // Specify that we want to use STJ as our serializer
            opts.UseSystemTextJsonForSerialization();

            opts.Policies.AllDocumentsSoftDeleted();
            opts.Policies.AllDocumentsAreMultiTenanted();

            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();
        
        builder.Host.UseWolverine(opts =>
        {
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

            opts.ApplicationAssembly = GetType().Assembly;
            
            opts.Discovery.IncludeAssembly(GetType().Assembly);

            // Let's build in some durability for transient errors
            opts.OnException<NpgsqlException>().Or<MartenCommandException>()
                .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

            // Apply the validation middleware *and* discover and register
            // Fluent Validation validators
            opts.UseFluentValidation();

            // Automatic transactional middleware
            opts.Policies.AutoApplyTransactions();

            // Opt into the transactional inbox for local 
            // queues
            opts.Policies.UseDurableLocalQueues();
            //
            // // Opt into the transactional inbox/outbox on all messaging
            // // endpoints
            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    
        });
        
        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        await host.Scenario(x =>
        {
            x.Post.Url($"/api/tenants/one/counters/{Guid.NewGuid()}/inc");
            x.StatusCodeShouldBe(404);
        });
        
        await host.Scenario(x =>
        {
            x.Post.Url($"/api/tenants/one/counters/{Guid.NewGuid()}/inc2");
            x.StatusCodeShouldBe(404);
        });

    }
}

public record Counter(Guid Id, int Count);

public static class CounterEndpoint
{
    // Endpoint


    [WolverinePost("/api/tenants/{tenant}/counters/{id}")]
    [EmptyResponse]
    public static IMartenOp Create(Guid id, IDocumentSession session)
    {
        var newCounter = new Counter(id, 0);
       
        return MartenOps.Store(newCounter);
    }

// Problem child
    [WolverinePost("/api/tenants/{tenant}/counters/{id}/inc")]
    public static (IResult, IMartenOp) Increment([Document(Required = true)] Counter counter)
    {
        if (counter == null)
        {
            return (Results.NotFound(), MartenOps.Nothing());
        }
        counter = counter with { Count = counter.Count + 1 };
        return (Results.Ok(), MartenOps.Store(counter));
    }

    #region sample_using_Document_required

    [WolverinePost("/api/tenants/{tenant}/counters/{id}/inc2")]
    public static IMartenOp Increment2([Document(Required = true)] Counter counter)
    {
        counter = counter with { Count = counter.Count + 1 };
        return MartenOps.Store(counter);
    }

    #endregion
}