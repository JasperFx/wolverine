using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;

namespace MartenTests.AggregateHandlerWorkflow;

// GH-3941: the store-agnostic EventsToAppend return type. Wolverine.Marten.Events,
// Wolverine.Polecat.Events and Wolverine.Fisher.Events are identical and store-named, so a handler
// compiled against more than one store could not name any of them and had to fall back to a bare
// IEnumerable<object> return. That fallback works but is positional: IEnumerable<T> is covariant, so
// EVERY reference-typed collection in a return tuple is a candidate and FirstOrDefault takes
// whichever lands first in Creates. Nothing fails at codegen and nothing fails at runtime - the wrong
// collection just becomes the events. ambiguous_sibling_collection_does_not_become_the_events is the
// fact that matters here; the other two would pass against the old fallback too.
//
// Note that this file imports BOTH Wolverine.Marten and Wolverine.Persistence.EventSourcing with no
// using alias, which is the combination a real store-agnostic handler needs -- the store integration
// plus [WriteModel]. That compiles only because the core type is not also called Events: naming it
// so collided with CS0104 on the two handler signatures at the bottom of this file.
public class core_events_return_3941 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RecordCoreDepositHandler))
                    .IncludeType(typeof(RecordAuditedDepositHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "core_events_return_3941";
                }).IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> givenAccount(decimal opening)
    {
        var streamId = Guid.NewGuid();
        await using var session = theHost.DocumentStore().LightweightSession();
        session.Events.StartStream<Account>(streamId, new AmountDeposited(opening));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    private async Task<Account> loadAccount(Guid streamId)
    {
        await using var session = theHost.DocumentStore().LightweightSession();
        return (await session.Events.AggregateStreamAsync<Account>(streamId,
            token: TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task the_core_events_type_is_appended_to_the_stream()
    {
        var streamId = await givenAccount(100m);

        await theHost.InvokeAsync(new RecordCoreDeposit(streamId, 25m));

        (await loadAccount(streamId)).Balance.ShouldBe(125m);
    }

    [Fact]
    public async Task every_event_in_the_collection_is_appended()
    {
        var streamId = await givenAccount(0m);

        await theHost.InvokeAsync(new RecordCoreDeposit(streamId, 10m, Repeat: 3));

        (await loadAccount(streamId)).Balance.ShouldBe(30m);
    }

    [Fact]
    public async Task ambiguous_sibling_collection_does_not_become_the_events()
    {
        var streamId = await givenAccount(50m);

        // The handler returns (EventsToAppend, IReadOnlyList<string>). Both are castable to
        // IEnumerable<object>, so under the old fallback alone the audit lines could be appended as
        // events instead - which throws nothing and simply corrupts the stream. Declaring EventsToAppend is
        // what makes the choice deterministic.
        await theHost.InvokeAsync(new RecordAuditedDeposit(streamId, 5m));

        (await loadAccount(streamId)).Balance.ShouldBe(55m);

        await using var session = theHost.DocumentStore().LightweightSession();
        var events = await session.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(2);
        events.ShouldAllBe(x => x.Data is AmountDeposited);
    }
}

public record RecordCoreDeposit(Guid AccountId, decimal Amount, int Repeat = 1);

public record RecordAuditedDeposit(Guid AccountId, decimal Amount);

public static class RecordCoreDepositHandler
{
    public static EventsToAppend Handle(RecordCoreDeposit command, [WriteModel] Account account)
        => new(Enumerable.Range(0, command.Repeat).Select(object (_) => new AmountDeposited(command.Amount)));
}

public static class RecordAuditedDepositHandler
{
    public static (EventsToAppend, IReadOnlyList<string>) Handle(
        RecordAuditedDeposit command,
        [WriteModel] Account account)
        => (new EventsToAppend { new AmountDeposited(command.Amount) },
            new[] { $"deposit of {command.Amount} against {command.AccountId}" });
}
