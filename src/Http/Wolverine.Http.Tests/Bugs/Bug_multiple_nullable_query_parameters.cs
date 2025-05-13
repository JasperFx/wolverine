using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public sealed class Bug_multiple_nullable_query_parameters
{
    [Fact]
    public async Task does_support_multiple_nullable_query_parameters()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder
            .Services
            .AddMarten(opts =>
            {
                // Establish the connection string to your Marten database
                opts.Connection(Servers.PostgresConnectionString);
                opts.DisableNpgsqlLogging = true;
            })
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery
                .DisableConventionalDiscovery()
                .IncludeType(typeof(NullableQueryParamsEndpoint));

            opts.ApplicationAssembly = GetType().Assembly;
        });

        builder.Services.AddWolverineHttp();

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        var result = await host.Scenario(s =>
        {
            s.Get.Url("/nullable-query-parameters");
        });

        var response = result.ReadAsJson<NullableQueryParamsResult>();

        response.ShouldBeEquivalentTo(
            new NullableQueryParamsResult(
                null,
                null,
                null,
                FilterMode.All));
    }
}


public static class NullableQueryParamsEndpoint
{
    [WolverineGet("/nullable-query-parameters")]
    public static NullableQueryParamsResult Get(
        [FromQuery] ResourceType? resourceType,
        [FromQuery] Guid? parentId,
        [FromQuery] int? take,
        [FromQuery] FilterMode filterMode = FilterMode.All)
    {
        return new NullableQueryParamsResult(resourceType, parentId, take, filterMode);
    }
}

public sealed record NullableQueryParamsResult(ResourceType? ResourceType, Guid? ParentId, int? Take, FilterMode? FilterMode);

public enum ResourceType
{
    Type1,
    Type2,
}

public enum FilterMode
{
    All,
    Pending,
    Completed,
}