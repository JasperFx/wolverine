#:sdk Microsoft.NET.Sdk.Web
#:project ../src/Http/Wolverine.Http/Wolverine.Http.csproj
#:project ../src/Wolverine.RuntimeCompilation/Wolverine.RuntimeCompilation.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5399");
builder.Host.UseWolverine();
builder.Services.AddWolverineHttp();

var app = builder.Build();
app.MapWolverineEndpoints();

await app.StartAsync();

using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5399") };

var failures = 0;

await expect("GET /journeys/42/legs", "42", () => client.GetStringAsync("/journeys/42/legs"));

await expect("POST /journeys/7/passengers", "7:Maverick", async () =>
{
    var response = await client.PostAsJsonAsync("/journeys/7/passengers", new AddPassengerPayload("Maverick"));
    return await response.Content.ReadAsStringAsync();
});

await app.StopAsync();

return failures;

async Task expect(string description, string expected, Func<Task<string>> call)
{
    string actual;
    try
    {
        actual = await call();
    }
    catch (Exception e)
    {
        actual = $"<{e.GetType().Name}: {e.Message}>";
    }

    if (actual == expected)
    {
        Console.WriteLine($"PASS  {description} -> \"{actual}\"");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL  {description} -> expected \"{expected}\", got \"{actual}\"");
    }
}

public record AddPassengerPayload(string Name);

public record AddPassengerCommand([FromRoute(Name = "journey-id")] int JourneyId, [FromBody] AddPassengerPayload Payload);

public static class JourneyEndpoints
{
    [WolverineGet("/journeys/{journey-id}/legs")]
    public static string CountLegs([FromRoute(Name = "journey-id")] int journeyId)
    {
        return journeyId.ToString();
    }

    [WolverinePost("/journeys/{journey-id}/passengers")]
    public static string AddPassenger([AsParameters] AddPassengerCommand command)
    {
        return $"{command.JourneyId}:{command.Payload.Name}";
    }
}
