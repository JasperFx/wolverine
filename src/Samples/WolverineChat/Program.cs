using JasperFx;
using JasperFx.Core;
using Wolverine;
using Wolverine.SignalR;
using WolverineChat;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

#region sample_configuring_signalr_on_server_side

builder.UseWolverine(opts =>
{
    // This is the only single line of code necessary
    // to wire SignalR services into Wolverine itself
    // This does also call IServiceCollection.AddSignalR()
    // to register DI services for SignalR as well
    opts.UseSignalR(o =>
    {
        // Optionally configure the SignalR HubOptions
        // for the WolverineHub
        o.ClientTimeoutInterval = 10.Seconds();
    });

    // Instead of self-hosting, it's also possible to
    // use Azure SignalR. Only one of the two SignalR
    // registrations are necessary. Both register the
    // required services in DI
    opts.UseAzureSignalR(hub =>
    {
        // Optionally configure the SignalR HubOptions
        // for the WolverineHub
        hub.ClientTimeoutInterval = 10.Seconds();
    }, service =>
    {
        // And optionally configure the Azure SignalR
        // options for the connection.
        service.ApplicationName = "wolverine";

        // You probably want one of these from your
        // configuration somehow
        service.ConnectionString = "Endpoint=https://myresource.service.signalr.net;AccessKey=...;Version=1.0;";
    });
    
    // Using explicit routing to send specific
    // messages to SignalR
    opts.Publish(x =>
    {
        // WolverineChatWebSocketMessage is a marker interface
        // for messages within this sample application that
        // is simply a convenience for message routing
        x.MessagesImplementing<WolverineChatWebSocketMessage>();
        x.ToSignalR();
    });
});

#endregion

builder.Services.AddHostedService<Pinging>();

#region sample_using_map_wolverine_signalrhub

var app = builder.Build();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
    .WithStaticAssets();

// This line puts the SignalR hub for Wolverine at the 
// designated route for your clients
app.MapWolverineSignalRHub("/api/messages");

return await app.RunJasperFxCommands(args);

#endregion