using Alba;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using JasperFx;
using JasperFx.CommandLine;
using Shouldly;
using Wolverine.Tracking;

namespace ItemService.Tests;

public class end_to_end_for_dbcontext_not_integrated_with_outbox
{
    public end_to_end_for_dbcontext_not_integrated_with_outbox()
    {
        JasperFxEnvironment.AutoStartHost = true;
    }

    [Fact]
    public async Task run_through_the_handler()
    {
        using var host = await AlbaHost.For<Program>();

        var name = Guid.NewGuid().ToString();
        var tracked = await host.InvokeMessageAndWaitAsync(new CreateItemWithDbContextNotIntegratedWithOutboxCommand { Name = name });
        tracked.FindSingleTrackedMessageOfType<ItemCreatedInDbContextNotIntegratedWithOutbox>()
            .ShouldNotBeNull();

        using var nested = host.Services.CreateScope();
        var context = nested.ServiceProvider.GetRequiredService<ItemsDbContext>();

        var item = await context.Items.FirstOrDefaultAsync(x => x.Name == name);
        item.ShouldNotBeNull();
    }
}