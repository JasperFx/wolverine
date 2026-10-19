using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Mentorships;

public static class GetMentorshipByIdEndpoint
{
    [WolverineGet("/api/mentorships/{id}")]
    public static Mentorship? Get(Guid id, [Entity] Mentorship? mentorship) => mentorship;
}

public static class GetMentorshipsForMentorEndpoint
{
    [WolverineGet("/api/mentorships/mentor/{mentorId}")]
    public static Task<IReadOnlyList<Mentorship>> Get(Guid mentorId, IQuerySession session, CancellationToken ct)
        => session.Query<Mentorship>()
            .Where(m => m.MentorId == mentorId)
            .OrderByDescending(m => m.RequestedAt)
            .ToListAsync(ct);
}

public static class GetMentorshipsForMenteeEndpoint
{
    [WolverineGet("/api/mentorships/mentee/{menteeId}")]
    public static Task<IReadOnlyList<Mentorship>> Get(Guid menteeId, IQuerySession session, CancellationToken ct)
        => session.Query<Mentorship>()
            .Where(m => m.MenteeId == menteeId)
            .OrderByDescending(m => m.RequestedAt)
            .ToListAsync(ct);
}

public static class GetPendingRequestsEndpoint
{
    [WolverineGet("/api/mentorships/pending/{mentorId}")]
    public static Task<IReadOnlyList<Mentorship>> Get(Guid mentorId, IQuerySession session, CancellationToken ct)
        => session.Query<Mentorship>()
            .Where(m => m.MentorId == mentorId && m.Status == MentorshipStatus.Pending)
            .OrderBy(m => m.RequestedAt)
            .ToListAsync(ct);
}
