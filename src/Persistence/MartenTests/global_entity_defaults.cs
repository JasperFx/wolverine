using System.Diagnostics;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests;

public class global_entity_defaults : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.AutoApplyTransactions();

                // Set global defaults
                opts.EntityDefaults.OnMissing = OnMissing.ThrowException;

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "global_defaults";
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task global_default_changes_handler_behavior()
    {
        // With global OnMissing = ThrowException, a plain [Entity] handler should throw
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new UseGlobalThing1(Guid.NewGuid().ToString()));
        });
    }

    [Fact]
    public async Task attribute_override_wins_over_global()
    {
        // Explicit [Entity(OnMissing = Simple404)] should override the global ThrowException default
        // No exception should be thrown - it should just stop silently
        await _host.InvokeAsync(new UseGlobalThing2(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task end_to_end_with_good_data()
    {
        var thing = new GlobalThing();
        await _host.DocumentStore().BulkInsertDocumentsAsync([thing]);

        var tracked = await _host.InvokeMessageAndWaitAsync(new UseGlobalThing1(thing.Id));

        tracked.Sent.SingleMessage<UsedGlobalThing>()
            .Id.ShouldBe(thing.Id);
    }
}

public class GlobalThing
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
}

public record UseGlobalThing1(string Id);

public record UseGlobalThing2(string Id);

public record UsedGlobalThing(string Id);

public static class GlobalThingHandler
{
    // Uses plain [Entity] - should pick up global default (ThrowException)
    public static UsedGlobalThing Handle(UseGlobalThing1 command, [Entity] GlobalThing thing)
    {
        return new UsedGlobalThing(thing.Id);
    }

    // Explicit override to Simple404 - should NOT throw even though global is ThrowException
    public static UsedGlobalThing Handle(UseGlobalThing2 command,
        [Entity(OnMissing = OnMissing.Simple404)] GlobalThing thing)
    {
        return new UsedGlobalThing(thing.Id);
    }

    public static void Handle(UsedGlobalThing msg)
    {
        Debug.WriteLine("Used global thing " + msg.Id);
    }
}
