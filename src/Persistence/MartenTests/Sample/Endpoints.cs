using Marten;
using Microsoft.Extensions.Logging;

namespace MartenTests.Sample;

#region sample_MartenUsingEndpoint_with_ctor_injection

public class MartenUsingEndpoint
{
    private readonly ILogger<User> _logger;
    private readonly IQuerySession _session;

    public MartenUsingEndpoint(IQuerySession session, ILogger<User> logger)
    {
        _session = session;
        _logger = logger;
    }

    public Task<User> get_user_id(string id)
    {
        _logger.LogDebug("I loaded a user");
        return _session.LoadAsync<User>(id);
    }
}

#endregion

#region sample_MartenStaticEndpoint

public static class MartenStaticEndpoint
{
    public static Task<User> get_user_id(
        string id,

        // Gets passed in by Wolverine at runtime
        IQuerySession session,

        // Gets passed in by Wolverine at runtime
        ILogger<User> logger)
    {
        logger.LogDebug("I loaded a user");
        return session.LoadAsync<User>(id);
    }
}

#endregion