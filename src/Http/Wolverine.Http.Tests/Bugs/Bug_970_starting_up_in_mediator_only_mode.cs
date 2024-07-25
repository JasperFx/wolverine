using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_970_starting_up_in_mediator_only_mode
{
    [Fact]
    public async Task can_call_through()
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
            opts.Discovery.DisableConventionalDiscovery().IncludeType<GetProjectsHandler>();
            opts.ApplicationAssembly = GetType().Assembly;
            
            // Automatically Commit Marten transactions
            opts.Policies.AutoApplyTransactions();
            // Using Wolverine as Mediator
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
        });

        builder.Services.AddControllers();
        
        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });
        
        await host.Scenario(x =>
        {
            x.Get.Url("/projects");
            x.StatusCodeShouldBe(200);
        });
    }
}

public static class ProjectController 
{
    [WolverineGet("/projects", Name = "GetProjects")]
    public static async Task<IReadOnlyList<ProjectOverview>> Get(IMessageBus bus, HttpContext context)
    {
        var userId = Guid.NewGuid(); // just care that it's a 200 and not a 500

        var projects = await bus.InvokeAsync<IReadOnlyList<ProjectOverview>>(userId);


        return projects;
    }
}



public class ProjectOverview
{
    public Guid UserId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
}

public class Project
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
}

public class GetProjectsHandler(IQuerySession session)
{
    public async Task<IReadOnlyList<ProjectOverview>> Handle(Guid userId)
    {
        var projects = await session.Query<Project>()
            .Where(p => p.UserId == userId)
            .Select(project => new ProjectOverview
            {
                Id = project.Id,
                UserId = project.UserId,
                Name = project.Name,
                Category = project.Category
            })
            .ToListAsync();

        return projects;
    }
}