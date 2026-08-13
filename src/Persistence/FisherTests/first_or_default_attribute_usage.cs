using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace FisherTests;

// The Fisher half of the [FirstOrDefault] storage agnostic promise -- the handler below is character for
// character what the Marten, Polecat, RavenDb and EF Core suites run.
public class first_or_default_attribute_usage : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("first_or_default");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FiAlertDefaultsHandler));
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task the_parameter_is_null_when_nothing_is_stored()
    {
        // Fisher creates a document table lazily on first write, and querying a type whose table was never
        // created throws "no such table" rather than returning nothing -- the same Fisher characteristic
        // storage_attribute_routes_to_fisher_store leans on to assert a negative. So establish the table,
        // then empty it, which is the state an application is actually in once it has used the type.
        await using (var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            var seed = new FiAlertDefaults { Threshold = 1 };
            session.Store(seed);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);

            session.Delete(seed);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadFiAlertDefaults());

        tracked.Sent.SingleMessage<FiAlertDefaultsRead>()
            .Threshold.ShouldBe(-1);
    }

    [Fact]
    public async Task the_first_document_is_supplied_when_one_exists()
    {
        await using (var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(new FiAlertDefaults { Threshold = 42 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadFiAlertDefaults());

        tracked.Sent.SingleMessage<FiAlertDefaultsRead>()
            .Threshold.ShouldBe(42);
    }
}

public class FiAlertDefaults
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Threshold { get; set; }
}

public record ReadFiAlertDefaults;

public record FiAlertDefaultsRead(int Threshold);

public static class FiAlertDefaultsHandler
{
    public static FiAlertDefaultsRead Handle(ReadFiAlertDefaults command,
        [FirstOrDefault] FiAlertDefaults? defaults)
    {
        return new FiAlertDefaultsRead(defaults?.Threshold ?? -1);
    }

    public static void Handle(FiAlertDefaultsRead msg)
    {
    }
}
