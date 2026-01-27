using IntegrationTests;
using JasperFx.Resources;
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
    public using_efcore(AppFixture fixture) : base(fixture)
    {
    }
    
    

    [Fact]
    public async Task using_db_context_without_outbox()
    {
        await cleanItems();

        var command = new CreateItemCommand { Name = "Isaiah Pacheco" };

        await Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/ef/create");
            x.StatusCodeShouldBe(204);
        });

        using var nested = Host.Services.CreateScope();
        var context = nested.ServiceProvider.GetRequiredService<ItemsDbContext>();

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
            x.StatusCodeShouldBe(204);
        });

        using var nested = Host.Services.CreateScope();
        var context = nested.ServiceProvider.GetRequiredService<ItemsDbContext>();

        var item = await context.Items.Where(x => x.Name == command.Name).FirstOrDefaultAsync();
        item.ShouldNotBeNull();

        tracked.Sent.SingleMessage<ItemCreated>()
            .ShouldNotBeNull();
    }

    private async Task cleanItems()
    {
        await Host.ResetResourceState();
        
        var table = new Table("items");
        table.AddColumn<Guid>("Id").AsPrimaryKey();
        table.AddColumn<string>("Name");

        using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();

            await table.ApplyChangesAsync(conn);

            await conn.RunSqlAsync("delete from items");
            await conn.CloseAsync();
        }
    }
    
    
    [Fact]
    public async Task using_db_context_with_outbox_schedule()
    {
        await cleanItems();

        var command = new CreateItemCommand { Name = "Jerick McKinnon" };

        var (tracked, _) = await TrackedHttpCall(x =>
        {
            x.Post.Json(command).ToUrl("/ef/schedule");
            x.StatusCodeShouldBe(204);
        }, 60000);

        using var nested = Host.Services.CreateScope();
        var context = nested.ServiceProvider.GetRequiredService<ItemsDbContext>();

        var item = await context.Items.Where(x => x.Name == command.Name).FirstOrDefaultAsync();
        item.ShouldBeNull();

        var records = tracked.AllRecordsInOrder().ToArray();

        var scheduledMessage = tracked.Scheduled.SingleEnvelope<ItemCreated>();
        scheduledMessage.ScheduleDelay.ShouldNotBeNull();
        scheduledMessage.Message.ShouldBeOfType<ItemCreated>();
    }
    
    [Fact]
    public async Task using_db_context_with_outbox_schedule2()
    {
        await cleanItems();

        var command = new CreateItemCommand { Name = "Jerick McKinnon" };

        var (tracked, _) = await TrackedHttpCall(x =>
        {
            x.Post.Json(command).ToUrl("/ef/schedule2");
            x.StatusCodeShouldBe(204);
        });

        using var nested = Host.Services.CreateScope();
        var context = nested.ServiceProvider.GetRequiredService<ItemsDbContext>();

        var item = await context.Items.Where(x => x.Name == command.Name).FirstOrDefaultAsync();
        item.ShouldBeNull();

        var scheduledMessage = tracked.Scheduled.SingleEnvelope<ItemCreated>();
        scheduledMessage.ScheduleDelay.ShouldNotBeNull();
        scheduledMessage.Message.ShouldBeOfType<ItemCreated>();
    }

    [Fact]
    public async Task using_db_context_with_outbox_schedule_nodb()
    {
        await cleanItems();

        var command = new CreateItemCommand { Name = "Jerick McKinnon" };

        var (tracked, _) = await TrackedHttpCall(x =>
        {
            x.Post.Json(command).ToUrl("/ef/schedule_nodb");
            x.StatusCodeShouldBe(204);
        });

        var scheduledMessage = tracked.Scheduled.SingleEnvelope<ItemCreated>();
        scheduledMessage.ScheduleDelay.ShouldNotBeNull();
        scheduledMessage.Message.ShouldBeOfType<ItemCreated>();
    }
}