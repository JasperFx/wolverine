using JasperFx.CodeGeneration;
using Marten;
using JasperFx;
using ProjectManagement.Api;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// GH-5703: a plain `dotnet run` of this sample has no launch settings, so it runs in Production, where Wolverine 7.0
// expects pre-generated code. A sample compiles its generated code at runtime in every environment instead. Dynamic
// rather than Auto, which would write the generated code into the project
builder.Services.CritterStackDefaults(x => x.Production.GeneratedCodeMode = TypeLoadMode.Dynamic);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("marten");
    opts.Connection(connectionString);
})
// More on this in a bit...
.IntegrateWithWolverine()

// Minor optimization
.UseLightweightSessions();

builder.Services.AddWolverineHttp();

builder.Host.UseWolverine(opts =>
{
    // more in a bit...
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/api/project/create", async (CreateProject command, IDocumentSession session) =>
{
    var projectCreated = new ProjectCreated(command.Name);
    var adminAssigned = new AdminAssigned(command.Name);
    var events = new object[] { projectCreated, adminAssigned }
        .Concat(command.TeamMembers.Select(x => new TeamMemberAssigned(x))).ToArray();

    var id = session.Events.StartStream<Project>(events).Id;

    await session.SaveChangesAsync();

    // New Stream Id for the project we just created
    return Results.Ok(id);
});

app.MapWolverineEndpoints();

// Replaced the standard command line runner with the 
// Critter Stack's expanded options
return await app.RunJasperFxCommands(args);

// Just to make testing a little easier
public partial class Program {}

