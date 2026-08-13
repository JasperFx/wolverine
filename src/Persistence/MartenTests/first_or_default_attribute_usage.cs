using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests;

// [FirstOrDefault] is deliberately storage agnostic -- the same handler is valid on Marten, Polecat, Fisher,
// RavenDb or EF Core. This is the Marten proof; the sibling suites cover the others.
public class first_or_default_attribute_usage : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AlertDefaultsHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "first_or_default";
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync();

        // Each test decides what is in the table, so start from empty every time
        await _host.DocumentStore().Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(AlertDefaults));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_parameter_is_null_when_nothing_is_stored()
    {
        // The whole point of the "always optional" design: the handler still runs, and writes its own
        // fallback. No 404, no exception, no silently skipped handler.
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadAlertDefaults());

        tracked.Sent.SingleMessage<AlertDefaultsRead>()
            .Threshold.ShouldBe(-1);
    }

    [Fact]
    public async Task the_first_document_is_supplied_when_one_exists()
    {
        await _host.DocumentStore().BulkInsertDocumentsAsync([new AlertDefaults { Threshold = 42 }],
            cancellation: TestContext.Current.CancellationToken);

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadAlertDefaults());

        tracked.Sent.SingleMessage<AlertDefaultsRead>()
            .Threshold.ShouldBe(42);
    }
}

public class AlertDefaults
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Threshold { get; set; }
}

public record ReadAlertDefaults;

public record AlertDefaultsRead(int Threshold);

public static class AlertDefaultsHandler
{
    // No identity anywhere in the message -- this is exactly the singleton configuration shape that
    // [Entity] cannot express, because [Entity] requires an identity value to load by.
    public static AlertDefaultsRead Handle(ReadAlertDefaults command, [FirstOrDefault] AlertDefaults? defaults)
    {
        return new AlertDefaultsRead(defaults?.Threshold ?? -1);
    }

    // Gives the cascaded message a local route so the tracked session records it as Sent rather than
    // dropping it with NoRoutes
    public static void Handle(AlertDefaultsRead msg)
    {
    }
}
