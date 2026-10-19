using Marten;
using Marten.AspNetCore;
using Wolverine.Http;

namespace Speakers;

// Marten.AspNetCore streams raw JSON from PostgreSQL directly to the HTTP response
// without deserializing to C# objects and re-serializing — significant performance win.

public static class GetSpeakersEndpoint
{
    [WolverineGet("/api/speakers")]
    public static Task Get(IQuerySession session, HttpContext context)
        => session.Query<Speaker>().OrderBy(s => s.LastName).WriteArray(context);
}

public static class GetSpeakerByIdEndpoint
{
    [WolverineGet("/api/speakers/{id}")]
    public static Task Get(Guid id, IQuerySession session, HttpContext context)
        => session.Json.WriteById<Speaker>(id, context);
}

public static class GetAvailableMentorsEndpoint
{
    [WolverineGet("/api/speakers/mentors")]
    public static Task Get(IQuerySession session, HttpContext context)
        => session.Query<Speaker>()
            .Where(s => s.IsAvailableForMentoring && s.Type == SpeakerType.Experienced)
            .OrderBy(s => s.LastName)
            .WriteArray(context);
}

public static class SearchSpeakersByExpertiseEndpoint
{
    [WolverineGet("/api/speakers/search")]
    public static Task Get(string expertise, IQuerySession session, HttpContext context)
        => session.Query<Speaker>()
            .Where(s => s.Expertise.Contains(expertise))
            .WriteArray(context);
}
