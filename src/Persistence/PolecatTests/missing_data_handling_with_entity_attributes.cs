using System.Diagnostics;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace PolecatTests;

public class missing_data_handling_with_entity_attributes : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "other_things";
                }).IntegrateWithWolverine();
            }).StartAsync();

        await ((DocumentStore)_host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task just_swallow_the_exception_and_log()
    {
        // Point is no exceptions here at all
        await _host.InvokeAsync(new UsePcThing1(Guid.NewGuid().ToString()));
        await _host.InvokeAsync(new UsePcThing2(Guid.NewGuid().ToString()));
        await _host.InvokeAsync(new UsePcThing3(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task missing_data_goes_nowhere()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new UsePcThing1(Guid.NewGuid().ToString()));

        tracked.Sent.AllMessages().Any().ShouldBeFalse();
    }

    [Fact]
    public async Task end_to_end_with_good_data()
    {
        var thing = new PcThing();
        await using var insertSession = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        insertSession.Store(thing);
        await insertSession.SaveChangesAsync();

        var tracked = await _host.InvokeMessageAndWaitAsync(new UsePcThing1(thing.Id));

        tracked.Sent.SingleMessage<UsedPcThing>()
            .Id.ShouldBe(thing.Id);
    }

    [Fact]
    public async Task throw_exception_instead()
    {
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new UsePcThing4(Guid.NewGuid().ToString()));
        });
    }

    [Fact]
    public async Task throw_exception_instead_with_custom_message()
    {
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new UsePcThing5(Guid.NewGuid().ToString()));
        });

        ex.Message.ShouldContain("You stink!");
    }

    [Fact]
    public async Task throw_exception_with_guid_identity()
    {
        var id = Guid.NewGuid();
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new UsePcGuidThing1(id));
        });

        ex.Message.ShouldContain(id.ToString());
    }

    [Fact]
    public async Task throw_exception_with_guid_identity_and_custom_message()
    {
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new UsePcGuidThing2(Guid.NewGuid()));
        });

        ex.Message.ShouldContain("GuidThing not found");
    }

    [Fact]
    public async Task end_to_end_with_guid_identity_entity()
    {
        var guidThing = new PcGuidThing();
        await using var insertSession = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        insertSession.Store(guidThing);
        await insertSession.SaveChangesAsync();

        var tracked = await _host.InvokeMessageAndWaitAsync(new UsePcGuidThing1(guidThing.Id));

        tracked.Sent.SingleMessage<UsedPcGuidThing>()
            .Id.ShouldBe(guidThing.Id);
    }
}

public class PcThing
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
}

public record UsePcThing1(string Id);
public record UsePcThing2(string Id);
public record UsePcThing3(string Id);
public record UsePcThing4(string Id);
public record UsePcThing5(string Id);
public record UsedPcThing(string Id);

public static class PcThingHandler
{
    public static UsedPcThing Handle(UsePcThing1 command, [Entity] PcThing thing)
    {
        return new UsedPcThing(thing.Id);
    }

    public static UsedPcThing Handle(UsePcThing2 command,
        [Entity(OnMissing = OnMissing.ProblemDetailsWith400)] PcThing thing)
    {
        return new UsedPcThing(thing.Id);
    }

    public static UsedPcThing Handle(UsePcThing3 command,
        [Entity(OnMissing = OnMissing.ProblemDetailsWith404)] PcThing thing)
    {
        return new UsedPcThing(thing.Id);
    }

    public static UsedPcThing Handle(UsePcThing4 command,
        [Entity(OnMissing = OnMissing.ThrowException)] PcThing thing)
    {
        return new UsedPcThing(thing.Id);
    }

    public static UsedPcThing Handle(UsePcThing5 command,
        [Entity(OnMissing = OnMissing.ThrowException, MissingMessage = "You stink!")] PcThing thing)
    {
        return new UsedPcThing(thing.Id);
    }

    public static void Handle(UsedPcThing msg)
    {
        Debug.WriteLine("Used thing " + msg.Id);
    }
}

public class PcGuidThing
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public record UsePcGuidThing1(Guid Id);
public record UsePcGuidThing2(Guid Id);
public record UsedPcGuidThing(Guid Id);

public static class PcGuidThingHandler
{
    public static UsedPcGuidThing Handle(UsePcGuidThing1 command,
        [Entity(OnMissing = OnMissing.ThrowException)] PcGuidThing thing)
    {
        return new UsedPcGuidThing(thing.Id);
    }

    public static UsedPcGuidThing Handle(UsePcGuidThing2 command,
        [Entity(OnMissing = OnMissing.ThrowException, MissingMessage = "GuidThing not found")] PcGuidThing thing)
    {
        return new UsedPcGuidThing(thing.Id);
    }

    public static void Handle(UsedPcGuidThing msg)
    {
        Debug.WriteLine("Used guid thing " + msg.Id);
    }
}
