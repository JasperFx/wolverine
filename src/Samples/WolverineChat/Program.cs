using JasperFx;
using Wolverine;
using Wolverine.SignalR;
using WolverineChat;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

builder.UseWolverine(opts =>
{
    opts.UseSignalR();
    opts.PublishMessage<ResponseMessage>().ToSignalR();
    opts.PublishMessage<Ping>().ToSignalR();
});

builder.Services.AddHostedService<Pinging>();

var app = builder.Build();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
    .WithStaticAssets();

app.MapWolverineSignalRHub("/api/messages");

return await app.RunJasperFxCommands(args);