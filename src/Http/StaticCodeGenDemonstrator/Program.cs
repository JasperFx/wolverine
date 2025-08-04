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

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
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