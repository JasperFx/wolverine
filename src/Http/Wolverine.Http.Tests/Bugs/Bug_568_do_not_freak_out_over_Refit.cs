using Alba;
using IntegrationTests;
using JasperFx.Core.Reflection;
using Lamar;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Refit;
using Shouldly;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_568_do_not_freak_out_over_Refit
{
    [Fact]
    public async Task compile_darnit()
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Services.AddScoped<IUserService, UserService>();

        builder.Services.AddMarten(Servers.PostgresConnectionString)
            .IntegrateWithWolverine();
        
        builder.Services.AddRefitClient<ITestHttpClient>();
        
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });
        
        await host.Scenario(x =>
        {
            x.Post.Json(new Bug568Message1("Hey")).ToUrl("/refit1");
            x.StatusCodeShouldBe(204);
        });
    }
}

public interface ITestHttpClient
{
    [Get("/test{id}")]
    Task<string> GetAfzenderAsync(string id);
}

public record Bug568Message1(string Text);
public class Test1Endpoint
{
    [WolverinePost("/refit1")]
    public static OutgoingMessages Post(Bug568Message1 message, ITestHttpClient testCLient)
    {
        return [new Bug568Message2(message.Text)];
    }
}

public record Bug568Message2(string Text);
public class Test2Handler
{
    public static void Handle(Bug568Message2 message)
    {
        Console.WriteLine("I got a message!");
    }
}