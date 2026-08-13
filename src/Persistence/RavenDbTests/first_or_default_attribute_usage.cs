using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.RavenDb;
using Wolverine.Tracking;

namespace RavenDbTests;

// The RavenDb half of the [FirstOrDefault] storage agnostic promise -- the handler below is character for
// character what the Marten, Polecat, Fisher and EF Core suites run.
[Collection("raven")]
public class first_or_default_attribute_usage : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;
    private IHost _host = null!;

    public first_or_default_attribute_usage(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _store = _fixture.StartRavenStore();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(RvAlertDefaultsHandler));
                opts.Services.AddSingleton<IDocumentStore>(_store);
                opts.UseRavenDbPersistence();
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_parameter_is_null_when_nothing_is_stored()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadRvAlertDefaults());

        tracked.Sent.SingleMessage<RvAlertDefaultsRead>()
            .Threshold.ShouldBe(-1);
    }

    [Fact]
    public async Task the_first_document_is_supplied_when_one_exists()
    {
        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new RvAlertDefaults { Threshold = 42 },
                TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // RavenDb indexes asynchronously, so a query issued immediately after the write can legitimately
        // miss it. Wait for the write to be queryable rather than making this a timing flake.
        await waitForQueryable();

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadRvAlertDefaults());

        tracked.Sent.SingleMessage<RvAlertDefaultsRead>()
            .Threshold.ShouldBe(42);
    }

    private async Task waitForQueryable()
    {
        for (var i = 0; i < 20; i++)
        {
            using var session = _store.OpenAsyncSession();
            var found = await session.Query<RvAlertDefaults>()
                .Customize(x => x.WaitForNonStaleResults())
                .CountAsync(TestContext.Current.CancellationToken);

            if (found > 0) return;

            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }
    }
}

public class RvAlertDefaults
{
    public string Id { get; set; } = null!;
    public int Threshold { get; set; }
}

public record ReadRvAlertDefaults;

public record RvAlertDefaultsRead(int Threshold);

public static class RvAlertDefaultsHandler
{
    public static RvAlertDefaultsRead Handle(ReadRvAlertDefaults command,
        [FirstOrDefault] RvAlertDefaults? defaults)
    {
        return new RvAlertDefaultsRead(defaults?.Threshold ?? -1);
    }

    public static void Handle(RvAlertDefaultsRead msg)
    {
    }
}
