using JasperFx.Events;
using Marten.Events.Aggregation;
using MartenTests.AggregateHandlerWorkflow;

namespace MartenTests.TestHelpers;

public class LetterCountsProjection: SingleStreamProjection<LetterCounts, Guid>
{
    public override LetterCounts Evolve(LetterCounts snapshot, Guid id, IEvent e)
    {

        switch (e.Data)
        {
            case AEvent _:
                snapshot ??= new() { Id = id };
                snapshot.ACount++;
                break;

            case BEvent _:
                snapshot ??= new() { Id = id };
                snapshot.BCount++;
                break;

            case CEvent _:
                snapshot ??= new() { Id = id };
                snapshot.CCount++;
                break;

            case DEvent _:
                snapshot ??= new() { Id = id };
                snapshot.DCount++;
                break;
            
            case EEvent _:
                snapshot ??= new() { Id = id };
                snapshot.ECount++;
                break;
        }

        return snapshot;
    }
}