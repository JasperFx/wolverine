using Alba;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Oakton;
using Shouldly;
using Wolverine.Tracking;

namespace ItemService.Tests;

public class end_to_end
{
    public end_to_end()
    {
        OaktonEnvironment.AutoStartHost = true;
    }

    [Fact]
    public async Task run_through_the_handler()
    {
        using var host = await AlbaHost.For<Program>();

        var name = Guid.NewGuid().ToString();
        var tracked = await host.InvokeMessageAndWaitAsync(new CreateItemCommand { Name = name });
        tracked.FindSingleTrackedMessageOfType<ItemCreated>()
            .ShouldNotBeNull();


        using var nested = host.Services.CreateScope();
        var context = nested.ServiceProvider.GetRequiredService<ItemsDbContext>();

        var item = await context.Items.FirstOrDefaultAsync(x => x.Name == name);
        item.ShouldNotBeNull();
    }

    [Fact]
    public async Task run_through_controller()
    {
        var name = Guid.NewGuid().ToString();
        using var host = await AlbaHost.For<Program>();
        var tracked = await host.ExecuteAndWaitAsync(async () =>
        {
            await host.Scenario(x =>
            {
                var command = new CreateItemCommand { Name = name };
                x.Post.Json(command).ToUrl("/items/create2");
            });
        });

        tracked.FindSingleTrackedMessageOfType<ItemCreated>()
            .ShouldNotBeNull();

        using var nested = host.Services.CreateScope();
        var context = nested.ServiceProvider.GetRequiredService<ItemsDbContext>();

        var item = await context.Items.FirstOrDefaultAsync(x => x.Name == name);
        item.ShouldNotBeNull();
    }

    [Fact]
    public async Task execute_through_wolverine_http()
    {
        var name = Guid.NewGuid().ToString();
        using var host = await AlbaHost.For<Program>();
        var tracked = await host.ExecuteAndWaitAsync(async () =>
        {
            await host.Scenario(x =>
            {
                var command = new CreateItemCommand { Name = name };
                x.Post.Json(command).ToUrl("/items/create4");
                x.StatusCodeShouldBe(204);
            });
        });

        tracked.FindSingleTrackedMessageOfType<ItemCreated>()
            .ShouldNotBeNull();

        using var nested = host.Services.CreateScope();
        var context = nested.ServiceProvider.GetRequiredService<ItemsDbContext>();

        var item = await context.Items.FirstOrDefaultAsync(x => x.Name == name);
        item.ShouldNotBeNull();
    }
}