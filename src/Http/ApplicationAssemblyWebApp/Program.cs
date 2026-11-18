using JasperFx;
using Marten;
using Wolverine;
using Wolverine.Http;

namespace ApplicationAssemblyWebApp;

// GH-5667. The shape of a typical Critter Stack web application: Marten registered BEFORE UseWolverine(), and no
// ApplicationAssembly set anywhere. Leave it that way; the tests depend on nothing here naming the assembly.
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Resolved lazily, so the store never needs a database unless something asks for it
        builder.Services.AddMarten(services =>
        {
            var opts = new StoreOptions();
            opts.Connection(services.GetRequiredService<IConfiguration>().GetConnectionString("marten")!);
            return opts;
        });

        builder.Host.UseWolverine();
        builder.Services.AddWolverineHttp();

        var app = builder.Build();
        app.MapWolverineEndpoints();

        return await app.RunJasperFxCommands(args);
    }
}

public record PingApplication(string Name);

public record PongApplication(string Name);

public static class PingApplicationHandler
{
    public static PongApplication Handle(PingApplication ping) => new(ping.Name);
}

public static class PingApplicationEndpoint
{
    [WolverineGet("/application-assembly/ping")]
    public static string Get() => "pong";
}
