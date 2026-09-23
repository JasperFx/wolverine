using IntegrationTests;
using JasperFx.Events;
using Polecat;
using Polecat.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StronglyTypedIds;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.AggregateHandlerWorkflow;

public class strong_named_identifiers : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "strong_named";
                }).IntegrateWithWolverine();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)theStore).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task use_read_aggregate_by_itself()
    {
        var streamId = Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(streamId, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var bus = theHost.MessageBus();
        var aggregate = await bus.InvokeAsync<StrongLetterAggregate>(new FetchCounts(new LetterId(streamId)), TestContext.Current.CancellationToken);

        aggregate.ACount.ShouldBe(1);
        aggregate.BCount.ShouldBe(1);
        aggregate.CCount.ShouldBe(2);
    }

    [Fact]
    public async Task single_usage_of_write_aggregate()
    {
        var streamId = Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(streamId, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await theHost.InvokeAsync(new IncrementStrongA(new LetterId(streamId)));

        var bus = theHost.MessageBus();
        var aggregate = await bus.InvokeAsync<StrongLetterAggregate>(new FetchCounts(new LetterId(streamId)), TestContext.Current.CancellationToken);

        aggregate.ACount.ShouldBe(2);
        aggregate.BCount.ShouldBe(1);
        aggregate.CCount.ShouldBe(2);
    }

    [Fact]
    public async Task batch_query_usage_of_write_aggregate()
    {
        var stream1Id = Guid.NewGuid();
        var stream2Id = Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(stream1Id, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());

        session.Events.StartStream<StrongLetterAggregate>(stream2Id, new AEvent(), new BEvent(), new BEvent(),
            new AEvent());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await theHost.InvokeMessageAndWaitAsync(new IncrementBOnBoth(new LetterId(stream1Id), new LetterId(stream2Id)));

        var aggregate1 = await session.Events.FetchLatest<StrongLetterAggregate>(stream1Id, TestContext.Current.CancellationToken);
        aggregate1!.BCount.ShouldBe(2);

        var aggregate2 = await session.Events.FetchLatest<StrongLetterAggregate>(stream2Id, TestContext.Current.CancellationToken);
        aggregate2!.BCount.ShouldBe(3);
    }

    [Fact]
    public async Task batch_query_with_both_read_and_write_aggregate()
    {
        var stream1Id = Guid.NewGuid();
        var stream2Id = Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(stream1Id, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());

        session.Events.StartStream<StrongLetterAggregate>(stream2Id, new AEvent(), new BEvent(), new BEvent(),
            new AEvent(), new DEvent());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await theHost.InvokeMessageAndWaitAsync(new AddFrom(new LetterId(stream1Id), new LetterId(stream2Id)));

        var aggregate1 = await session.Events.FetchLatest<StrongLetterAggregate>(stream1Id, TestContext.Current.CancellationToken);
        aggregate1!.BCount.ShouldBe(3);
        aggregate1.ACount.ShouldBe(3);
        aggregate1.DCount.ShouldBe(1);

        var aggregate2 = await session.Events.FetchLatest<StrongLetterAggregate>(stream2Id, TestContext.Current.CancellationToken);
        aggregate2!.BCount.ShouldBe(2);
    }

    // GH-4515. This is the one test Marten's twin of this file has and Polecat's did not, and it is the
    // exact case GH-4514 fixed: UpdatedAggregate resolving an identity that is a strong typed wrapper
    // rather than a bare Guid. Without it, Polecat's own UpdatedAggregate.ResolveToGuidType ValueTypeInfo
    // branch and UpdatedAggregateIdentity.Resolve MemberAccessVariable branch were never executed by any
    // test -- shipped code with no coverage, in a file hand-copied from the one that did have it.
    [Fact]
    public async Task use_updated_aggregate_as_the_response_with_a_strong_typed_identifier()
    {
        var streamId = Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(streamId, new AEvent(), new BEvent());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (tracked, updated) = await theHost
            .InvokeMessageAndWaitAsync<StrongLetterAggregate>(new RaiseStrong(new LetterId(streamId), 2, 3));

        tracked.Sent.AllMessages().ShouldBeEmpty();

        // The aggregate came back already holding the events this message appended, which is what says
        // UpdatedAggregate resolved a strong typed identity rather than refusing it.
        updated.ShouldNotBeNull();
        updated.ACount.ShouldBe(3);
        updated.BCount.ShouldBe(4);

        // NOTE: Marten's twin of this test also asserts updated.Id.ShouldBe(new LetterId(streamId)).
        // That assertion cannot pass on Polecat today, and the gap is NOT in Wolverine: a plain
        // session.Events.FetchLatest<StrongLetterAggregate>(streamId) returns an aggregate whose
        // strong typed Id is default(LetterId) too. Wolverine's UpdatedAggregate is faithfully handing
        // back what the store gave it. Raise upstream against Polecat, then restore the assertion here.
    }

}

public record IncrementStrongA(LetterId Id);

public record RaiseStrong(LetterId Id, int A, int B);

public record AddFrom(LetterId Id1, LetterId Id2);

public record IncrementBOnBoth(LetterId Id1, LetterId Id2);

public record FetchCounts(LetterId Id);

public static class StrongLetterHandler
{
    public static StrongLetterAggregate Handle(FetchCounts counts,
        [ReadAggregate] StrongLetterAggregate aggregate) => aggregate;

    public static (UpdatedAggregate, Events) Handle(RaiseStrong command,
        [WriteAggregate] StrongLetterAggregate aggregate)
    {
        var events = new Events();
        for (int i = 0; i < command.A; i++)
        {
            events.Add(new AEvent());
        }

        for (int i = 0; i < command.B; i++)
        {
            events.Add(new BEvent());
        }

        return (new UpdatedAggregate(), events);
    }

    public static AEvent Handle(IncrementStrongA command, [WriteAggregate] StrongLetterAggregate aggregate)
    {
        return new();
    }

    public static void Handle(
        IncrementBOnBoth command,
        [WriteAggregate(nameof(IncrementBOnBoth.Id1))] IEventStream<StrongLetterAggregate> stream1,
        [WriteAggregate(nameof(IncrementBOnBoth.Id2))] IEventStream<StrongLetterAggregate> stream2
    )
    {
        stream1.AppendOne(new BEvent());
        stream2.AppendOne(new BEvent());
    }

    public static IEnumerable<object> Handle(
        AddFrom command,
        [WriteAggregate(nameof(AddFrom.Id1))] StrongLetterAggregate _,
        [ReadAggregate(nameof(AddFrom.Id2))] StrongLetterAggregate readOnly)
    {
        for (int i = 0; i < readOnly.ACount; i++)
        {
            yield return new AEvent();
        }

        for (int i = 0; i < readOnly.BCount; i++)
        {
            yield return new BEvent();
        }

        for (int i = 0; i < readOnly.CCount; i++)
        {
            yield return new CEvent();
        }

        for (int i = 0; i < readOnly.DCount; i++)
        {
            yield return new DEvent();
        }
    }
}

[StronglyTypedId(Template.Guid)]
public readonly partial struct LetterId;

public class StrongLetterAggregate
{
    public StrongLetterAggregate()
    {
    }

    public LetterId Id { get; set; }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }

    public void Apply(AEvent _) => ACount++;
    public void Apply(BEvent _) => BCount++;
    public void Apply(CEvent _) => CCount++;
    public void Apply(DEvent _) => DCount++;
}
