using Marten;
using Marten.Events;
using StronglyTypedIds;
using Wolverine.Http;
using Wolverine.Marten;

namespace WolverineWebApi.Marten;

public static class StrongLetterHandler
{
    #region sample_using_strong_typed_id_as_route_argument

    [WolverineGet("/sti/aggregate/longhand/{id}")]
    public static ValueTask<StrongLetterAggregate> Handle2(LetterId id, IDocumentSession session) =>
        session.Events.FetchLatest<StrongLetterAggregate>(id.Value);
    
    // This is an equivalent to the endpoint above 
    [WolverineGet("/sti/aggregate/{id}")]
    public static StrongLetterAggregate Handle(
        [ReadAggregate] StrongLetterAggregate aggregate) => aggregate;

    #endregion

    [WolverinePost("/sti/incrementa"), EmptyResponse]
    public static AEvent Handle(IncrementStrongA command, [WriteAggregate] StrongLetterAggregate aggregate)
    {
        return new();
    }
    
    [WolverinePost("/sti/incrementa2/{id}"), EmptyResponse]
    public static AEvent Handle2([WriteAggregate] StrongLetterAggregate aggregate)
    {
        return new();
    }

    [WolverinePost("/sti/multiples")]
    public static void Handle(
        IncrementBOnBoth command,
        [WriteAggregate(nameof(IncrementBOnBoth.Id1))] IEventStream<StrongLetterAggregate> stream1,
        [WriteAggregate(nameof(IncrementBOnBoth.Id2))] IEventStream<StrongLetterAggregate> stream2
        )
    {
        stream1.AppendOne(new BEvent());
        stream2.AppendOne(new BEvent());
    }

    [WolverinePost("/sti/writeread"), EmptyResponse]
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

public record IncrementStrongA(LetterId Id);

public record AddFrom(LetterId Id1, LetterId Id2);

public record IncrementBOnBoth(LetterId Id1, LetterId Id2);

public record FetchCounts(LetterId Id);



#region sample_letter_id

[StronglyTypedId(Template.Guid)]
public readonly partial struct LetterId;

#endregion

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

    public void Apply(AEvent e)
    {
        ACount++;
    }

    public void Apply(BEvent e)
    {
        BCount++;
    }

    public void Apply(CEvent e)
    {
        CCount++;
    }

    public void Apply(DEvent e)
    {
        DCount++;
    }
}

public record AEvent;

public record BEvent;

public record CEvent;

public record DEvent;

