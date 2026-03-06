using Castle.Components.DictionaryAdapter.Xml;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;

namespace MartenTests.Dcb.University;

public record ChangeCourseCapacity(CourseId CourseId, int Capacity);

/// <summary>
/// Changes a course's capacity. Validates the course exists and capacity differs.
/// Uses DCB to query by CourseId tag.
/// </summary>
[WolverineIgnore]
public static class ChangeCourseCapacityHandler
{
    public static async Task Handle(ChangeCourseCapacity command, IDocumentSession session)
    {
        var query = new EventTagQuery()
            .Or<CourseCreated, CourseId>(command.CourseId)
            .Or<CourseCapacityChanged, CourseId>(command.CourseId);
        var boundary = await session.Events.FetchForWritingByTags<CourseState>(query);

        var state = boundary.Aggregate;
        if (state is not { Created: true })
            throw new InvalidOperationException("Course with given id does not exist");

        if (command.Capacity == state.Capacity)
            return; // No change needed

        var @event = new CourseCapacityChanged(FacultyId.Default, command.CourseId, command.Capacity);
        boundary.AppendOne(@event);
    }
}

public static class WithDcbChangeCourseCapacityHandler
{
    public class State
    {
        public bool Created { get; private set; }
        public int Capacity { get; private set; }

        public void Apply(CourseCreated e)
        {
            Created = true;
            Capacity = e.Capacity;
        }

        public void Apply(CourseCapacityChanged e)
        {
            Capacity = e.Capacity;
        }
    }
    
    public static EventTagQuery Load(ChangeCourseCapacity command)
        => new EventTagQuery()
            .Or<CourseCreated, CourseId>(command.CourseId)
            .Or<CourseCapacityChanged, CourseId>(command.CourseId);

    public static HandlerContinuation Validate(
        ChangeCourseCapacity command,
        State state,
        ILogger logger)
    {
        if (state is not { Created: true })
        {
            logger.LogDebug("Course with given id {CourseId} does not exist", command.CourseId);
            return HandlerContinuation.Stop;
        }

        return HandlerContinuation.Continue;
    }
    
    public static CourseCapacityChanged? Handle(ChangeCourseCapacity command, State state)
    {
        return command.Capacity != state.Capacity
            ? new CourseCapacityChanged(FacultyId.Default, command.CourseId, command.Capacity)
            : null;
    }
}

public static class AggregateHandlerChangeCourseCapacityHandler
{
    public class Course
    {
        public bool Created { get; private set; }
        public int Capacity { get; private set; }

        public void Apply(CourseCreated e)
        {
            Created = true;
            Capacity = e.Capacity;
        }

        public void Apply(CourseCapacityChanged e)
        {
            Capacity = e.Capacity;
        }
    }
    
    public static EventTagQuery Load(ChangeCourseCapacity command)
        => new EventTagQuery()
            .Or<CourseCreated, CourseId>(command.CourseId)
            .Or<CourseCapacityChanged, CourseId>(command.CourseId);

    public static HandlerContinuation Validate(
        ChangeCourseCapacity command,
        
        
        Course state,
        ILogger logger)
    {
        if (state is not { Created: true })
        {
            logger.LogDebug("Course with given id {CourseId} does not exist", command.CourseId);
            return HandlerContinuation.Stop;
        }

        return HandlerContinuation.Continue;
    }
    
    public static CourseCapacityChanged? Handle(ChangeCourseCapacity command, 
        
        // TODO -- see if we could auto-register this with Marten?
        [WriteAggregate] 
        Course state)
    {
        return command.Capacity != state.Capacity
            ? new CourseCapacityChanged(FacultyId.Default, command.CourseId, command.Capacity)
            : null;
    }
}

