using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs
{
    public class Bug_766_status_code_overwritten
    {
        [Theory]
        [InlineData(400)]
        [InlineData(404)]
        [InlineData(500)]
        public async Task status_code_should_not_be_overwritten_when_set_by_handler(int code)
        {
            var builder = WebApplication.CreateBuilder([]);

            // config
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
            }).IntegrateWithWolverine().UseLightweightSessions();

            builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(GetType().Assembly));

            builder.Services.AddWolverineHttp();

            // This is using Alba, which uses WebApplicationFactory under the covers
            await using var host = await AlbaHost.For(builder, app =>
            {
                app.MapWolverineEndpoints();
            });

            await host.Scenario(x =>
            {
                x.Get.Url($"/status/{code}");
                x.StatusCodeShouldBe(code);
            });
        }

        [Fact]
        public async Task status_code_should_be_204_when_no_body_and_default_status_code()
        {
            var builder = WebApplication.CreateBuilder([]);

            // config
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
            }).IntegrateWithWolverine().UseLightweightSessions();

            builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(GetType().Assembly));

            builder.Services.AddWolverineHttp();

            // This is using Alba, which uses WebApplicationFactory under the covers
            await using var host = await AlbaHost.For(builder, app =>
            {
                app.MapWolverineEndpoints();
            });

            await host.Scenario(x =>
            {
                x.Get.Url($"/status/{200}");
                x.StatusCodeShouldBe(204);
            });
        }
    }

    public static class StatusCodeEndpoints
    {
        [EmptyResponse]
        [WolverineGet("/status/{code}")]
        public static void SetStatusCode(int code, HttpContext httpContext) => httpContext.Response.StatusCode = code;
    }
}