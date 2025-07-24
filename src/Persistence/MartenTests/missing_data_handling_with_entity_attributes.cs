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

// This is really general Wolverine behavior, but it's easiest to do this
// with Marten, so it's here.
public class missing_data_handling_with_entity_attributes : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "other_things";
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task just_swallow_the_exception_and_log()
    {
        // Point is no exceptions here at all
        // Manually checking logs in Debug
        await _host.InvokeAsync(new UseThing1(Guid.NewGuid().ToString()));
        await _host.InvokeAsync(new UseThing2(Guid.NewGuid().ToString()));
        await _host.InvokeAsync(new UseThing3(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task missing_data_goes_nowhere()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new UseThing1(Guid.NewGuid().ToString()));
        
        tracked.Sent.AllMessages().Any().ShouldBeFalse();
    }

    [Fact]
    public async Task end_to_end_with_good_data()
    {
        var thing = new Thing();
        await _host.DocumentStore().BulkInsertDocumentsAsync([thing]);
        
        var tracked = await _host.InvokeMessageAndWaitAsync(new UseThing1(thing.Id));
        
        tracked.Sent.SingleMessage<UsedThing>()
            .Id.ShouldBe(thing.Id);
    }

    [Fact]
    public async Task throw_exception_instead()
    {
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new UseThing4(Guid.NewGuid().ToString()));
        });
    }
    
    [Fact]
    public async Task throw_exception_instead_with_custom_message()
    {
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new UseThing5(Guid.NewGuid().ToString()));
        });
        
        ex.Message.ShouldContain("You stink!");
    }
}

public class Thing
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
}

public record UseThing1(string Id);

public record UseThing2(string Id);

public record UseThing3(string Id);

public record UseThing4(string Id);

public record UseThing5(string Id);

public record UsedThing(string Id);

public static class ThingHandler
{
    public static UsedThing Handle(UseThing1 command, [Entity] Thing thing)
    {
        return new UsedThing(thing.Id);
    }

    public static UsedThing Handle(UseThing2 command, [Entity(OnMissing = OnMissing.ProblemDetailsWith400)] Thing thing)
    {
        return new UsedThing(thing.Id);
    }

    public static UsedThing Handle(UseThing3 command, [Entity(OnMissing = OnMissing.ProblemDetailsWith404)] Thing thing)
    {
        return new UsedThing(thing.Id);
    }

    public static UsedThing Handle(UseThing4 command, [Entity(OnMissing = OnMissing.ThrowException)] Thing thing)
    {
        return new UsedThing(thing.Id);
    }

    public static UsedThing Handle(UseThing5 command,
        [Entity(OnMissing = OnMissing.ThrowException, MissingMessage = "You stink!")] Thing thing)
    {
        return new UsedThing(thing.Id);
    }

    public static void Handle(UsedThing msg)
    {
        Debug.WriteLine("Used thing " + msg.Id);
    }
}