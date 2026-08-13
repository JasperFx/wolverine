using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests;

// The Polecat half of the [FirstOrDefault] storage agnostic promise -- the handler below is character for
// character what the Marten, Fisher, RavenDb and EF Core suites run.
public class first_or_default_attribute_usage : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PcAlertDefaultsHandler));
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "first_or_default";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var store = (DocumentStore)_host.Services.GetRequiredService<IDocumentStore>();
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();

        // Polecat's cleaner has no per-type delete; this schema is dedicated to these tests anyway
        await store.Advanced.Clean.DeleteAllDocumentsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_parameter_is_null_when_nothing_is_stored()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadPcAlertDefaults());

        tracked.Sent.SingleMessage<PcAlertDefaultsRead>()
            .Threshold.ShouldBe(-1);
    }

    [Fact]
    public async Task the_first_document_is_supplied_when_one_exists()
    {
        await using (var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(new PcAlertDefaults { Threshold = 42 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadPcAlertDefaults());

        tracked.Sent.SingleMessage<PcAlertDefaultsRead>()
            .Threshold.ShouldBe(42);
    }
}

public class PcAlertDefaults
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Threshold { get; set; }
}

public record ReadPcAlertDefaults;

public record PcAlertDefaultsRead(int Threshold);

public static class PcAlertDefaultsHandler
{
    public static PcAlertDefaultsRead Handle(ReadPcAlertDefaults command,
        [FirstOrDefault] PcAlertDefaults? defaults)
    {
        return new PcAlertDefaultsRead(defaults?.Threshold ?? -1);
    }

    public static void Handle(PcAlertDefaultsRead msg)
    {
    }
}
