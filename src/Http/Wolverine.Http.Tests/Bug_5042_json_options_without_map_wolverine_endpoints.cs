using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.Tests;

// GH-5042. Only MapWolverineEndpoints() swapped in ASP.NET Core's JsonOptions, so HttpHandlers mapped
// directly -- CritterWatch's embedded console maps its pre-generated handlers into the host's route table --
// serialized with a bare JsonSerializerOptions: PascalCase and numeric enums
public class Bug_5042_json_options_without_map_wolverine_endpoints
{
    [Fact]
    public void uses_the_asp_net_core_json_options_without_map_wolverine_endpoints()
    {
        var services = new ServiceCollection();
        services.AddWolverineHttp();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<WolverineHttpOptions>().JsonSerializerOptions.Value;

        // ASP.NET Core's defaults, not a bare JsonSerializerOptions
        options.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.CamelCase);
        options.PropertyNameCaseInsensitive.ShouldBeTrue();
    }

    [Fact]
    public void honors_configure_http_json_options_without_map_wolverine_endpoints()
    {
        var services = new ServiceCollection();
        services.ConfigureHttpJsonOptions(x => x.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddWolverineHttp();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<WolverineHttpOptions>().JsonSerializerOptions.Value;

        options.Converters.OfType<JsonStringEnumConverter>().ShouldNotBeEmpty();
    }
}
