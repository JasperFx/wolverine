using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using Oakton;
using Swashbuckle.AspNetCore.Swagger;
using Wolverine.Runtime;

namespace Wolverine.Http;

public class OpenApiInput : NetCoreInput
{
    
    [Description("name of the swagger doc you want to retrieve, as configured in your startup class")]
    public string Swaggerdoc { get; set; } = null;
    
    [Description("relative path where the Swagger will be output, defaults to stdout")]
    public string? OutputFlag { get; set; } = null;
    
    [Description("a specific host to include in the Swagger output")]
    public string? HostFlag { get; set; } = null;
    
    [Description("a specific basePath to include in the Swagger output")]
    public string? BasePathFlag { get; set; } = null;
    
    [Description("output Swagger in the V2 format rather than V3")]
    public bool? SerializeAsV2Flag { get; set; } = false;
    
    [Description("exports swagger in a yaml format")]
    public bool? YamlFlag { get; set; } = false;
}
[Description("Export OpenAPI definition", Name = "openapi")]
public class OpenApiCommand: OaktonAsyncCommand<OpenApiInput>
{
    public override async Task<bool> Execute(OpenApiInput input)
    {
        
        using IHost host = input.BuildHost();
        // var wolverineRuntime = host.Services.GetService<WolverineRuntime>()!;
        // await wolverineRuntime.StartAsync(CancellationToken.None);
        
        //TODO: Wait for Wolverine to map all http endpoints
        //TODO: Don't start the host application
        
        await host.StartAsync();
        ISwaggerProvider swaggerProvider = host.Services.GetRequiredService<ISwaggerProvider>();
        OpenApiDocument swagger = swaggerProvider.GetSwagger(input.Swaggerdoc, input.HostFlag, input.BasePathFlag);
        var paths = swagger.Paths.Count;
        await host.StopAsync();
        // await wolverineRuntime.StopAsync(CancellationToken.None);
        
        var outputPath = input.OutputFlag is null ? null : Path.Combine(Directory.GetCurrentDirectory(), input.OutputFlag);
        await using var streamWriter = outputPath is null ? Console.Out : File.CreateText(outputPath);
        
        
        IOpenApiWriter writer;
        if (input.YamlFlag is true)
            writer = new OpenApiYamlWriter(streamWriter);
        else
            writer = new OpenApiJsonWriter(streamWriter);
        
        if (input.SerializeAsV2Flag is true)
            swagger.SerializeAsV2(writer);
        else
            swagger.SerializeAsV3(writer);

        Console.WriteLine($"Swagger JSON/YAML successfully written to '{input.OutputFlag}' containing {paths} paths");
        
        return true;
    }
}