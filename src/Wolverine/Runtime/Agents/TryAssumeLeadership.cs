using Wolverine.Util.Dataflow;

namespace Wolverine.Runtime.Agents;

public record TryAssumeLeadership : IInternalMessage// Send with Ack
{
    public Guid? CurrentLeaderId { get; set; }
}