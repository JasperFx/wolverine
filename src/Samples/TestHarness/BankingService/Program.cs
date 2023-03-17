using BankingService;
using Oakton;
using Wolverine;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine();
builder.Services.AddSingleton<IAccountService, RealAccountService>();

// Add services to the container.

var app = builder.Build();

app.MapWolverineEndpoints();

return await app.RunOaktonCommands(args);