using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_309_service_dependencies_should_be_deep_on_injected_arguments
{
    [Fact]
    public async Task discover_session_is_required_by_constructor_argument_of_handler_method()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddScoped<IItemRepository, ItemRepository>();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<CreateItemHandler>();
            }).StartAsync();

        var (_, created) = await host.InvokeMessageAndWaitAsync<ItemCreated>(new CreateItem());

        var store = host.Services.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        var item = await session.LoadAsync<Item>(created.Id);
        item.ShouldNotBeNull();
    }
}

public record CreateItem;


public class CreateItemHandler
{
    public ItemCreated Handle(CreateItem command, IItemRepository repository)
    {
        var item = new Item();
        repository.Save(item);
        return new ItemCreated(item.Id, "Something");
    }
}

public interface IItemRepository
{
    void Save(Item item);
}

public class ItemRepository : IItemRepository
{
    private readonly IDocumentSession _session;

    public ItemRepository(IDocumentSession session)
    {
        _session = session;
    }

    public void Save(Item item)
    {
        _session.Store(item);
    }
}