using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Meetings;

public static class GetMeetingGroupsEndpoint
{
    [WolverineGet("/api/meeting-groups")]
    public static Task<IReadOnlyList<MeetingGroup>> Get(IQuerySession session, CancellationToken ct)
        => session.Query<MeetingGroup>().ToListAsync(ct);
}

public static class GetMeetingGroupByIdEndpoint
{
    [WolverineGet("/api/meeting-groups/{id}")]
    public static MeetingGroup? Get(Guid id, [Entity] MeetingGroup? group) => group;
}

public static class GetMeetingGroupMeetingsEndpoint
{
    [WolverineGet("/api/meeting-groups/{id}/meetings")]
    public static Task<IReadOnlyList<Meeting>> Get(Guid id, IQuerySession session, CancellationToken ct)
        => session.Query<Meeting>()
            .Where(m => m.MeetingGroupId == id)
            .OrderByDescending(m => m.TermStartDate)
            .ToListAsync(ct);
}

public static class GetMeetingsEndpoint
{
    [WolverineGet("/api/meetings")]
    public static Task<IReadOnlyList<Meeting>> Get(IQuerySession session, CancellationToken ct)
        => session.Query<Meeting>().OrderByDescending(m => m.TermStartDate).ToListAsync(ct);
}

public static class GetMeetingByIdEndpoint
{
    [WolverineGet("/api/meetings/{id}")]
    public static Meeting? Get(Guid id, [Entity] Meeting? meeting) => meeting;
}

/// <summary>
/// The Meetings module's view of a user. A Member only exists because the Registrations module
/// published <see cref="MeetingGroupMonolith.NewUserRegisteredEvent"/> and this module handled
/// it, so this is the endpoint that makes that cascade observable from outside the process.
/// </summary>
public static class GetMemberByIdEndpoint
{
    [WolverineGet("/api/members/{id}")]
    public static Member? Get(Guid id, [Entity] Member? member) => member;
}
