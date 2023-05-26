using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Http.Tests;

public class DocumentationSamples
{
    public static async Task include_assemblies()
    {
        #region sample_programmatically_scan_assemblies

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This gives you the option to programmatically
                // add other assemblies to the discovery of HTTP endpoints
                // or message handlers
                var assembly = Assembly.Load("my other assembly name that holds HTTP endpoints or handlers");
                opts.Discovery.IncludeAssembly(assembly);
            }).StartAsync();

        #endregion
    }
}

public record GoToColor(string Color);

public class ConditionalRedirectHandler
{
    #region sample_conditional_IResult_return

    [WolverineGet("/choose/color")]
    public IResult Redirect(GoToColor request)
    {
        switch (request.Color)
        {
            case "Red":
                return Results.Redirect("/red");

            case "Green":
                return Results.Redirect("/green");

            default:
                return Results.Content("Choose red or green!");
        }
    }

    #endregion
}