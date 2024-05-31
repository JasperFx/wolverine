using System.Drawing;
using System.Runtime.InteropServices.ComTypes;
using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Marten;

public class multi_tenanted_session_factory_without_wolverine
{
    [Fact]
    public async Task can_do_the_tenancy_detection()
    {
        // Arrange -- and sorry, it's a bit of "Arrange" to get an IHost
        var builder = WebApplication.CreateBuilder([]);

        builder.Services
            .AddMarten(options =>
            {
                options.Connection(Servers.PostgresConnectionString);
                options.DatabaseSchemaName = "mt_no_wolverine";
                options.Schema.For<ColorDoc>().MultiTenanted();
            });

        #region sample_using_AddMartenTenancyDetection

        builder.Services.AddMartenTenancyDetection(tenantId =>
        {
            tenantId.IsQueryStringValue("tenant");
            tenantId.DefaultIs("default-tenant");
        });

            #endregion
        
        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapGet("/color", (IQuerySession session) => session.LoadAsync<ColorDoc>("main"));
        });

        var store = host.DocumentStore();

        // Store the blue doc
        using (var session = store.LightweightSession("blue"))
        {
            session.Store(new ColorDoc
            {
                Id = "main",
                Number = 1
            });

            await session.SaveChangesAsync();
        }
        
        // Store the green doc
        using (var session = store.LightweightSession("green"))
        {
            session.Store(new ColorDoc
            {
                Id = "main",
                Number = 2
            });

            await session.SaveChangesAsync();
        }

        var blueDoc = await host.GetAsJson<ColorDoc>("/color?tenant=blue");
        blueDoc.Number.ShouldBe(1);
        
        var greenDoc = await host.GetAsJson<ColorDoc>("/color?tenant=green");
        greenDoc.Number.ShouldBe(2);
    }
    
    [Fact]
    public async Task can_do_the_tenancy_detection_for_query_session()
    {
        // Arrange -- and sorry, it's a bit of "Arrange" to get an IHost
        var builder = WebApplication.CreateBuilder([]);

        builder.Services
            .AddMarten(options =>
            {
                options.Connection(Servers.PostgresConnectionString);
                options.DatabaseSchemaName = "mt_no_wolverine";
                options.Schema.For<ColorDoc>().MultiTenanted();
            });

        #region sample_using_AddMartenTenancyDetection

        builder.Services.AddMartenTenancyDetection(tenantId =>
        {
            tenantId.IsQueryStringValue("tenant");
            tenantId.DefaultIs("default-tenant");
        });

            #endregion
        
        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapGet("/color", (IQuerySession session) => session.LoadAsync<ColorDoc>("main"));
        });

        var store = host.DocumentStore();

        // Store the blue doc
        using (var session = store.LightweightSession("blue"))
        {
            session.Store(new ColorDoc
            {
                Id = "main",
                Number = 1
            });

            await session.SaveChangesAsync();
        }
        
        // Store the green doc
        using (var session = store.LightweightSession("green"))
        {
            session.Store(new ColorDoc
            {
                Id = "main",
                Number = 2
            });

            await session.SaveChangesAsync();
        }

        var blueDoc = await host.GetAsJson<ColorDoc>("/color?tenant=blue");
        blueDoc.Number.ShouldBe(1);
        
        var greenDoc = await host.GetAsJson<ColorDoc>("/color?tenant=green");
        greenDoc.Number.ShouldBe(2);
    }
}

public class ColorDoc
{
    public string Id { get; set; }
    public int Number { get; set; }
}