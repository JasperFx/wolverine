#:sdk Microsoft.NET.Sdk.Web
#:project ../src/Http/Wolverine.Http/Wolverine.Http.csproj
#:project ../src/Wolverine.RuntimeCompilation/Wolverine.RuntimeCompilation.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Http;

// A route-template parameter (`{id}`) annotated [FromQuery] is emitted TWICE in the endpoint's
// ApiDescription: once with Source=Path (from the route template) and once with Source=Query (from
// [FromQuery]). Two parameters with the same name is invalid OpenAPI. It also crashes .NET's OpenAPI
// XML-documentation transformer, whose per-<param> lookup does
//     operation.Parameters.SingleOrDefault(p => p.Name == comment.Name)
// throwing "Sequence contains more than one matching element" and failing ALL of
// /openapi/{document}.json (HTTP 500). This regressed between 6.16 and 6.21.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5401");
builder.Host.UseWolverine();
builder.Services.AddWolverineHttp();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();
app.MapWolverineEndpoints();
await app.StartAsync();

var provider = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
var descriptions = provider.ApiDescriptionGroups.Items.SelectMany(g => g.Items).ToList();

var failures = 0;

var getThing = descriptions.FirstOrDefault(d =>
    string.Equals(d.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
    && (d.RelativePath?.Contains("things/{id}") ?? false));

if (getThing is null)
{
    failures++;
    Console.WriteLine("FAIL  could not find the GET things/{id} ApiDescription");
}
else
{
    var idParams = getThing.ParameterDescriptions.Where(p => p.Name == "id").ToList();
    var sources = string.Join(", ", idParams.Select(p => p.Source?.Id ?? "<null>"));
    expect($"count of 'id' parameters (sources: {sources})", "1", idParams.Count.ToString());
}

await app.StopAsync();
return failures;

void expect(string description, string expected, string actual)
{
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

public static class ThingEndpoints
{
    /// <summary>Gets a thing by id.</summary>
    /// <param name="id">The identifier of the thing.</param>
    [WolverineGet("/things/{id}")]
    public static string Get([FromQuery] string id) => id;
}
