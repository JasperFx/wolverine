using JasperFx.CodeGeneration;
using JasperFx;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Http;
using Wolverine.Runtime.Handlers;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
});

builder.Services.AddOpenApi();

builder.Services.AddWolverineHttp();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapWolverineEndpoints();

return await app.RunJasperFxCommands(args);

public static class TestEndpoint
{
    [WolverinePost("test")]
    public static async Task Post(IMessageBus bus)
    {
        await bus.SendAsync(new BadMessage(1));
    }
}

public sealed record BadMessage(int BadMessageId);

public static class BadMessageHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnAnyException()
            .MoveToErrorQueue()
            .And((_, context, ex) =>
            {
                if (context.Envelope?.Message is BadMessage message)
                {
                    // do something
                }
                return ValueTask.CompletedTask;
            });
    }

    public static void Handle(BadMessage message)
    {
        throw new Exception("Test!");
    }
}