using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;

namespace MartenTests.Saga;

public interface ISequencedMessage
{
    string ItemId { get; }
    int Sequence { get; }
}

public record Step1(string ItemId, int Sequence) : ISequencedMessage;
public record Step2(string ItemId, int Sequence) : ISequencedMessage;
public record Step3(string ItemId, int Sequence) : ISequencedMessage;
public record LastStep(string ItemId, int Sequence) : ISequencedMessage;

public class Item
{
    public string Id { get; set; }
    public List<int> Sequences { get; set; } = new();
}

public class SequentialSaga : Wolverine.Saga
{
    public string Id { get; set; }

    public List<ISequencedMessage> Messages { get; set; } = new();
    public int LastSequence { get; set; }

    public (IMartenOp, OutgoingMessages) StartOrHandle(Step1 step1, [Entity] Item item)
    {
        var outgoing = new OutgoingMessages();
        if (step1.Sequence == LastSequence + 1)
        {
            item ??= new() { Id = step1.ItemId };
            item.Sequences.Add(step1.Sequence);
            LastSequence = step1.Sequence;
            
            // Try to recover other messages
            while (Messages.Any())
            {
                var next = Messages.FirstOrDefault(x => x.Sequence == LastSequence + 1);
                if (next == null) break;
                
                outgoing.Add(next);
            }
            
            return (MartenOps.Store(item), outgoing);
        }
        
        Messages.Add(step1);
        return (MartenOps.Nothing(), outgoing);
    }
}