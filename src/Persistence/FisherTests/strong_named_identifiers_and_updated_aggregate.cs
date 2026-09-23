using Fisher;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StronglyTypedIds;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
///     GH-4515. Fisher had <b>no</b> strong-typed-identifier coverage and <b>no</b>
///     <see cref="UpdatedAggregate" /> coverage of any kind -- <c>Wolverine.Fisher/UpdatedAggregate.cs</c>
///     is a shipped file that no test executed, and a census turned up zero matches for
///     <c>StronglyTypedId</c> anywhere under FisherTests.
/// </summary>
/// <remarks>
///     That mattered because Fisher's <c>UpdatedAggregate</c> is a hand copy of Marten's, right down to the
///     <c>ResolveToGuidType</c> / <c>UpdatedAggregateIdentity.Resolve</c> blocks -- and GH-4514 had just
///     found a real defect in the original. A copy of code with a recent bug in it, running under no test
///     at all, is exactly the drift a shared three-store matrix exists to catch.
/// </remarks>
public class strong_named_identifiers_and_updated_aggregate : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("fi_strong_id");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FiStrongLetterHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Projections.Snapshot<FiStrongLetter>(SnapshotLifecycle.Inline);
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    private async Task<Guid> startStreamAsync()
    {
        var streamId = Guid.NewGuid();
        var store = theHost.Services.GetRequiredService<IDocumentStore>();

        await using var session = store.LightweightSession();
        session.Events.StartStream<FiStrongLetter>(streamId, new FiAEvent(), new FiBEvent());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    [Fact]
    public async Task write_aggregate_with_a_strong_typed_identifier()
    {
        var streamId = await startStreamAsync();

        await theHost.InvokeMessageAndWaitAsync(new FiIncrementA(new FiLetterId(streamId)));

        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var aggregate = await session.Events
            .FetchLatest<FiStrongLetter>(streamId, TestContext.Current.CancellationToken);

        aggregate!.ACount.ShouldBe(2);
        aggregate.BCount.ShouldBe(1);
    }

    [Fact]
    public async Task read_aggregate_with_a_strong_typed_identifier()
    {
        var streamId = await startStreamAsync();

        var (_, counts) = await theHost
            .InvokeMessageAndWaitAsync<FiStrongLetter>(new FiFetchCounts(new FiLetterId(streamId)));

        counts.ShouldNotBeNull();
        counts.ACount.ShouldBe(1);
        counts.BCount.ShouldBe(1);
    }

    // The one that matters: UpdatedAggregate resolving an identity that is a strong typed wrapper rather
    // than a bare Guid. This is the exact shape GH-4514 fixed on Marten, against a file Fisher copied.
    [Fact]
    public async Task use_updated_aggregate_as_the_response_with_a_strong_typed_identifier()
    {
        var streamId = await startStreamAsync();

        var (tracked, updated) = await theHost
            .InvokeMessageAndWaitAsync<FiStrongLetter>(new FiRaiseStrong(new FiLetterId(streamId), 2, 3));

        tracked.Sent.AllMessages().ShouldBeEmpty();

        // The aggregate came back already holding the events this message appended
        updated.ShouldNotBeNull();
        updated.ACount.ShouldBe(3);
        updated.BCount.ShouldBe(4);
    }
}

[StronglyTypedId(Template.Guid)]
public readonly partial struct FiLetterId;

public record FiAEvent;

public record FiBEvent;

public record FiIncrementA(FiLetterId Id);

public record FiFetchCounts(FiLetterId Id);

public record FiRaiseStrong(FiLetterId Id, int A, int B);

public class FiStrongLetter
{
    // Strong typed on the aggregate as well as the command -- the Marten and Polecat twins of this suite
    // do the same, and the id member has to match or aggregate id discovery cannot bind them.
    public FiLetterId Id { get; set; }

    public int ACount { get; set; }
    public int BCount { get; set; }

    public void Apply(FiAEvent _) => ACount++;
    public void Apply(FiBEvent _) => BCount++;
}

public static class FiStrongLetterHandler
{
    public static FiStrongLetter Handle(FiFetchCounts _, [ReadAggregate] FiStrongLetter aggregate) => aggregate;

    public static FiAEvent Handle(FiIncrementA _, [WriteAggregate] FiStrongLetter aggregate) => new();

    public static (UpdatedAggregate, Events) Handle(FiRaiseStrong command,
        [WriteAggregate] FiStrongLetter aggregate)
    {
        var events = new Events();
        for (var i = 0; i < command.A; i++)
        {
            events.Add(new FiAEvent());
        }

        for (var i = 0; i < command.B; i++)
        {
            events.Add(new FiBEvent());
        }

        return (new UpdatedAggregate(), events);
    }
}
