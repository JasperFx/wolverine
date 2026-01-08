using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Http;
using Wolverine.Http.Transport;

var builder = WebApplication.CreateBuilder(args);
const string httpNamedClient = "https://extenal/";
builder.UseWolverine(opts =>
{
    var transport = new HttpTransport();
    opts.Transports.Add(transport);
    // Publish all messages to the external http endpoint using the named http client
    // This will publish anarray of envelopes to the external endpoint
    opts.PublishAllMessages().ToHttpEndpoint(httpNamedClient);
    
    // To publish individual messages instead of batches, use this instead
    // opts.PublishAllMessages().ToHttpEndpoint(httpNamedClient).SendInline();
    
    // If the httpendpoint supports native scheduled sends, use this instead
    // opts.PublishAllMessages().ToHttpEndpoint(httpNamedClient, supportsNativeScheduledSend: true).SendInline();
});
builder.Services.AddWolverineHttp();
// Configure the named http client to point to the external wolverine server
builder.Services.AddHttpClient(
    httpNamedClient,
    client =>
    {
        client.BaseAddress = new Uri("https://where-ever-you want-message-to-go/");
        //client.DefaultRequestHeaders.Add("Authorization", $"Bearer eyJ***");
    });
// To handle the array of messages over HTTP, send them to https://where-your-app-with-message-handlers/_wolverine/batch/queue
// To handle single message over HTTP, send them to https://where-your-app-with-message-handlers/_wolverine/invoke
// Register the WolverineHttpTransportClient for sending messages over HTTP
builder.Services.AddScoped<IWolverineHttpTransportClient, WolverineHttpTransportClient>();
// You can have your own implementation of IWolverineHttpTransportClient if you need custom behavior
// builder.Services.AddScoped<IWolverineHttpTransportClient, MyWolverineHttpTransportClient>();
var app = builder.Build();
app.MapWolverineEndpoints();
app.MapPost(
    "/test-command", async (TestCommand command, IMessageBus bus) =>
    {
        await bus.SendAsync(command);
        return Results.Ok();
    });
app.MapWolverineHttpTransportEndpoints();

app.Run();

public record TestCommand(string message);

public class TestCommandHandler
{
    public TestCommand1 Handle(TestCommand command)
    {
        Console.WriteLine(
            $"Handled command with message: {command.message}");
        return new TestCommand1(command.message + "x");
    }
}

public record TestCommand1(string message);

public class TestCommand1Handler(IMessageContext messageBus)
{
    public void Handle(TestCommand1 command)
    {
        Console.WriteLine($"Handled TestCommand1 with message: {command.message}");
    }
}
