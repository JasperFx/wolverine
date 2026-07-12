using Alba;
using IntegrationTests;
using JasperFx;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.Http.Tests.LoadProfileOnly;
using Xunit;

namespace Wolverine.Http.Tests;

// End-to-end coverage for named EF Core load profiles: [Entity(Profile = "...")] selects a
// pre-declared include graph (HasLoadProfile) at the call site. The "full" profile pulls the
// child Lines; the "summary" profile loads the root only; a missing id still 404s.
//
// Endpoints live in their own Marten-free assembly (Wolverine.Http.Tests.LoadProfileOnly),
// discovery is pinned there.
public class LoadProfile_integration : IAsyncLifetime
{
    private IAlbaHost _host = null!;
    private readonly Guid _orderId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddDbContextWithWolverineIntegration<LoadProfileDbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(LoadProfileEndpoints).Assembly;
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "loadprofile");
            opts.UseEntityFrameworkCoreTransactions();
            opts.Discovery.DisableConventionalDiscovery();
        });

        builder.Services.AddWolverineHttp();

        _host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints());

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LoadProfileDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            create schema if not exists loadprofile;
            create table if not exists loadprofile.profile_orders ("Id" uuid primary key, "Name" varchar(200) not null);
            create table if not exists loadprofile.profile_order_lines ("Id" uuid primary key, "OrderId" uuid not null references loadprofile.profile_orders("Id"), "Product" varchar(200) not null);
            delete from loadprofile.profile_order_lines;
            delete from loadprofile.profile_orders;
            """);

        db.Orders.Add(new ProfileOrder
        {
            Id = _orderId,
            Name = "Acme",
            Lines =
            {
                new ProfileOrderLine { Id = Guid.NewGuid(), Product = "Widget" },
                new ProfileOrderLine { Id = Guid.NewGuid(), Product = "Gadget" }
            }
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task full_profile_loads_the_child_collection()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/loadprofile/full/{_orderId}");
            x.StatusCodeShouldBeOk();
        });

        var response = await result.ReadAsJsonAsync<ProfileLoadResponse>();
        response.ShouldNotBeNull();
        response.Id.ShouldBe(_orderId);
        response.LineCount.ShouldBe(2);
    }

    [Fact]
    public async Task summary_profile_loads_the_root_only()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/loadprofile/summary/{_orderId}");
            x.StatusCodeShouldBeOk();
        });

        var response = await result.ReadAsJsonAsync<ProfileLoadResponse>();
        response.ShouldNotBeNull();
        response.Id.ShouldBe(_orderId);
        response.LineCount.ShouldBe(0);
    }

    [Fact]
    public async Task missing_entity_404s()
    {
        await _host.Scenario(x =>
        {
            x.Get.Url($"/loadprofile/full/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });
    }
}
