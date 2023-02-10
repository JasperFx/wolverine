using IntegrationTests;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_efcore : IntegrationContext
{
    [Fact]
    public async Task using_db_context_without_outbox()
    {
        await cleanItems();
        
        var command = new CreateItemCommand { Name = "Isaiah Pacheco" };

        await Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/ef/create");
        });

        using var nested = Host.Services.As<IContainer>().GetNestedContainer();
        var context = nested.GetInstance<ItemsDbContext>();

        var item = await context.Items.Where(x => x.Name == command.Name).FirstOrDefaultAsync();
        item.ShouldNotBeNull();
    }
    
    [Fact]
    public async Task using_db_context_with_outbox()
    {
        await cleanItems();
        
        var command = new CreateItemCommand { Name = "Jerick McKinnon" };

        var (tracked, _) = await TrackedHttpCall(x =>
        {
            x.Post.Json(command).ToUrl("/ef/publish");
        });

        using var nested = Host.Services.As<IContainer>().GetNestedContainer();
        var context = nested.GetInstance<ItemsDbContext>();

        var item = await context.Items.Where(x => x.Name == command.Name).FirstOrDefaultAsync();
        item.ShouldNotBeNull();

        tracked.Sent.SingleMessage<ItemCreated>()
            .ShouldNotBeNull();
    }

    private async Task cleanItems()
    {
        var table = new Table("items");
        table.AddColumn<Guid>("Id").AsPrimaryKey();
        table.AddColumn<string>("Name");
        
        using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();

            await table.ApplyChanges(conn);
            
            await conn.RunSql("delete from items");
            await conn.CloseAsync();
        }
        

    }

    public using_efcore(AppFixture fixture) : base(fixture)
    {
    }
}