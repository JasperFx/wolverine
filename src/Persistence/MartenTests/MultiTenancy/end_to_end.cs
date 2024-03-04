using Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace MartenTests.MultiTenancy;

public class end_to_end : MultiTenancyContext
{
    public end_to_end(MultiTenancyFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task send_tenant_related_message()
    {
        var store = Fixture.Host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.Clean.DeleteAllDocumentsAsync();
        
        var tracked = await Fixture.Host.SendMessageAndWaitAsync(new CreateTenantDoc("Tom", 11),
            new DeliveryOptions { TenantId = "tenant2" });

        using var session = store.LightweightSession("tenant2");

        var loaded = await session.LoadAsync<TenantDoc>("Tom");
        loaded.Number.ShouldBe(11);
    }
}

public class TenantDoc
{
    public string Id { get; set; }
    public string Name { get; set; }
    
    public int Number { get; set; }
}

public record CreateTenantDoc(string Id, int Number);

public record NumberTenantDoc(string Id, int Number);

public static class TenantDocHandler
{
    public static NumberTenantDoc Handle(CreateTenantDoc command, IDocumentSession session)
    {
        var doc = new TenantDoc { Id = command.Id };
            session.Store(doc);
        return new NumberTenantDoc(command.Id, command.Number);
    }

    public static async Task Handle(NumberTenantDoc command, IDocumentSession session)
    {
        var doc = await session.LoadAsync<TenantDoc>(command.Id);
        doc.Number = command.Number;
        
        session.Store(doc);
    }
}

