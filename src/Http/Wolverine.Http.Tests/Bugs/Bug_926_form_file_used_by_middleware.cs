using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Wolverine.Attributes;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_926_form_file_used_by_middleware
{
    [Fact]
    public async Task should_only_codegen_the_form_file_once()
    {
        var builder = WebApplication.CreateBuilder([]);
        
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "myapp";

            // Specify that we want to use STJ as our serializer
            opts.UseSystemTextJsonForSerialization();

            opts.Policies.AllDocumentsSoftDeleted();
            opts.Policies.AllDocumentsAreMultiTenanted();

            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();
        
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.ApplicationAssembly = GetType().Assembly;
        });
        
        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        await host.Scenario(x =>
        {
            x.Post.Url("/api/files/upload").ContentType("application/x-www-form-urlencoded");

            
        });
    }
}

public static class FileUploadEndpoint
{
    [Middleware(
        typeof(FileLengthValidationMiddleware), 
        typeof(FileExtensionValidationMiddleware)
    )]
    [WolverinePost("/api/files/upload")]
    public static async Task<Ok> HandleAsync(IFormFile file)
    {
        // todo, generate filename, write mapping to table
        // todo, create sideeffect to write file with new filename

        return TypedResults.Ok(); // return new filename
    }
}

public static class FileLengthValidationMiddleware
{
    public static void Before(IFormFile file)
    {
        // todo, return ProblemDetail if validation fails
    }
}

public static class FileExtensionValidationMiddleware
{
    public static void Before(IFormFile file)
    {
        // todo, return ProblemDetail if validation fails
    }
}