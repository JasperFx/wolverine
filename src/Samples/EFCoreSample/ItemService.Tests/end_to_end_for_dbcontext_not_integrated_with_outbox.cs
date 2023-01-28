using Alba;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.EntityFrameworkCore;
using Oakton;
using Shouldly;
using Wolverine.Tracking;

namespace ItemService.Tests;

public class end_to_end_for_dbcontext_not_integrated_with_outbox
{
    public end_to_end_for_dbcontext_not_integrated_with_outbox()
    {
        OaktonEnvironment.AutoStartHost = true;
    }
    
    [Fact]
    public async Task run_through_the_handler()
    {
        using var host = await AlbaHost.For<Program>();

        var name = Guid.NewGuid().ToString();
        var tracked = await host.InvokeMessageAndWaitAsync(new CreateItemWithDbContextNotIntegratedWithOutboxCommand { Name = name });
        tracked.FindSingleTrackedMessageOfType<ItemCreatedInDbContextNotIntegratedWithOutbox>()
            .ShouldNotBeNull();
        
        
        using var nested = host.Services.As<IContainer>().GetNestedContainer();
        var context = nested.GetInstance<ItemsDbContext>();

        var item = await context.Items.FirstOrDefaultAsync(x => x.Name == name);
        item.ShouldNotBeNull();
    }
}