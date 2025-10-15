using Marten;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class strong_typed_identifiers : IntegrationContext
{
    public strong_typed_identifiers(AppFixture fixture) : base(fixture)
    {

    }
    
    [Fact]
    public async Task use_read_aggregate_by_itself()
    {
        var streamId = Guid.NewGuid();
        using var session = Host.DocumentStore().LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(streamId, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        await session.SaveChangesAsync();

        var result = await Scenario(x =>
        {
            x.Get.Url("/sti/aggregate/" + streamId);
        });

        var aggregate = result.ReadAsJson<StrongLetterAggregate>();

        aggregate.ACount.ShouldBe(1);
        aggregate.BCount.ShouldBe(1);
        aggregate.CCount.ShouldBe(2);
    }

    [Fact]
    public async Task single_usage_of_write_aggregate()
    {
        var streamId = Guid.NewGuid();
        using var session = Host.DocumentStore().LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(streamId, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        await session.SaveChangesAsync();

        await Scenario(x =>
        {
            x.Post.Json(new IncrementStrongA(new LetterId(streamId))).ToUrl("/sti/incrementa");
            x.StatusCodeShouldBe(204);
        });
        
        var result = await Scenario(x =>
        {
            x.Get.Url("/sti/aggregate/" + streamId);
        });

        var aggregate = result.ReadAsJson<StrongLetterAggregate>();
        
        aggregate.ACount.ShouldBe(2);
        aggregate.BCount.ShouldBe(1);
        aggregate.CCount.ShouldBe(2);
    }

    [Fact]
    public async Task batch_query_usage_of_write_aggregate()
    {
        var stream1Id = Guid.NewGuid();
        var stream2Id = Guid.NewGuid();
        using var session = Host.DocumentStore().LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(stream1Id, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        
        session.Events.StartStream<StrongLetterAggregate>(stream2Id, new AEvent(), new BEvent(), new BEvent(),
            new AEvent());
        await session.SaveChangesAsync();

        await Scenario(x =>
        {
            x.Post.Json(new IncrementBOnBoth(new LetterId(stream1Id), new LetterId(stream2Id))).ToUrl("/sti/multiples");
            x.StatusCodeShouldBe(204);
        });

        var aggregate1 = await session.Events.FetchLatest<StrongLetterAggregate>(stream1Id);
        aggregate1.BCount.ShouldBe(2);
        
        var aggregate2 = await session.Events.FetchLatest<StrongLetterAggregate>(stream2Id);
        aggregate2.BCount.ShouldBe(3);
        
    }

    [Fact]
    public async Task batch_query_with_both_read_and_write_aggregate()
    {
        var stream1Id = Guid.NewGuid();
        var stream2Id = Guid.NewGuid();
        using var session = Host.DocumentStore().LightweightSession();
        session.Events.StartStream<StrongLetterAggregate>(stream1Id, new AEvent(), new BEvent(), new CEvent(),
            new CEvent());
        
        session.Events.StartStream<StrongLetterAggregate>(stream2Id, new AEvent(), new BEvent(), new BEvent(),
            new AEvent(), new DEvent());
        await session.SaveChangesAsync();

        await Host.Scenario(x =>
        {
            x.Post.Json(new AddFrom(new LetterId(stream1Id), new LetterId(stream2Id)))
                .ToUrl("/sti/writeread");
            x.StatusCodeShouldBe(204);
        });

        var aggregate1 = await session.Events.FetchLatest<StrongLetterAggregate>(stream1Id);
        aggregate1.BCount.ShouldBe(3);
        aggregate1.ACount.ShouldBe(3);
        aggregate1.DCount.ShouldBe(1);
        
        var aggregate2 = await session.Events.FetchLatest<StrongLetterAggregate>(stream2Id);
        aggregate2.BCount.ShouldBe(2);
    }
}