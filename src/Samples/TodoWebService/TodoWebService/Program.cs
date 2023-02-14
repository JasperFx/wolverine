using Marten;
using Oakton;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMarten(opts =>
{
    opts.Connection(builder.Configuration.GetConnectionString("Marten"));
    opts.DatabaseSchemaName = "todo";
})
    .IntegrateWithWolverine()
    .ApplyAllDatabaseChangesOnStartup();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Let's add in Wolverine HTTP endpoints to the routing tree
app.MapWolverineEndpoints();

return await app.RunOaktonCommands(args);