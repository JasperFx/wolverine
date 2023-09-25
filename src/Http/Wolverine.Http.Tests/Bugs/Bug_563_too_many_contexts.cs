using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_563_too_many_contexts
{
    [Fact]
    public async Task use_them_all()
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Services.AddScoped<MyService1>();
        builder.Services.AddScoped<MyService2>();
        builder.Host.UseWolverine();

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapGet("/", async (IMessageBus bus) => {
                await bus.PublishAsync(new MyMessage());
                return Results.Ok("ok!");
            });
        });

        await host.GetAsText("/");

    }
}

public class MyService1 {
    public MyService1(IMessageContext arg) { }
}

public class MyService2 {
    public MyService2(MyService1 svc1, IMessageBus bus) { }
}

public record MyMessage();

public class MyMessageHandler {
    public void Handle(MyMessage command, MyService2 service2) { }
}