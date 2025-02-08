#region sample_Quickstart_Program

using JasperFx;
using Quickstart;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// The almost inevitable inclusion of Swashbuckle:)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// For now, this is enough to integrate Wolverine into
// your application, but there'll be *many* more
// options later of course :-)
builder.Host.UseWolverine();

// Some in memory services for our application, the
// only thing that matters for now is that these are
// systems built by the application's IoC container
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<IssueRepository>();

var app = builder.Build();

// An endpoint to create a new issue that delegates to Wolverine as a mediator
app.MapPost("/issues/create", (CreateIssue body, IMessageBus bus) => bus.InvokeAsync(body));

// An endpoint to assign an issue to an existing user that delegates to Wolverine as a mediator
app.MapPost("/issues/assign", (AssignIssue body, IMessageBus bus) => bus.InvokeAsync(body));

// Swashbuckle inclusion
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("/swagger"));

// Opt into using JasperFx for command line parsing
// to unlock built in diagnostics and utility tools within
// your Wolverine application
return await app.RunJasperFxCommands(args);

#endregion