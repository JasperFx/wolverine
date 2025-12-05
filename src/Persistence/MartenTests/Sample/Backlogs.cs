using Wolverine.Marten;

namespace MartenTests.Sample;

public class BacklogItem;

public class Sprint;

public record BackLotItemCommitted(Guid SprintId);
public record CommitToSprint(Guid BacklogItemId, Guid SprintId);

// This is utilizing Wolverine's "Aggregate Handler Workflow" 
// which is the Critter Stack's flavor of the "Decider" pattern
public static class CommitToSprintHandler
{
    public static Events Handle(
        // The actual command
        CommitToSprint command,

        // Current state of the back log item, 
        // and we may decide to make the commitment here
        [WriteAggregate] BacklogItem item,

        // Assuming that Sprint is event sourced, 
        // this is just a read only view of that stream
        [ReadAggregate] Sprint sprint)
    {
        // Use the item & sprint to "decide" if 
        // the system can proceed with the commitment
        return [new BackLotItemCommitted(command.SprintId)];
    }
}