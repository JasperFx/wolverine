using JasperFx.Events.Daemon;

namespace Wolverine.RDBMS;

/// <summary>
/// The advisory lock every Wolverine database store holds its leadership and polling locks with. It is
/// JasperFx's <see cref="IAdvisoryLock" /> plus an asynchronous liveness probe.
/// </summary>
/// <remarks>
/// GH-5135. <see cref="IAdvisoryLock.HasLock" /> is synchronous, and Wolverine's implementations do not
/// answer it from memory: they ping the session that holds the lock (GH-2602), because the database drops a
/// session-level lock the moment that session ends and the client cannot tell until it uses the connection.
/// That ping ran as blocking I/O on the leadership health-check tick. Wolverine's own callers use
/// <see cref="HasLockAsync" /> instead; <see cref="IAdvisoryLock.HasLock" /> stays only because the
/// JasperFx contract requires it.
/// </remarks>
public interface IWolverineAdvisoryLock : IAdvisoryLock
{
    /// <summary>
    /// True when this node still holds the lock identified by <paramref name="lockId" />, established by
    /// pinging the session that holds it. False for an id this node never took, and false (with every held
    /// id cleared) when the ping finds the session gone.
    /// </summary>
    /// <param name="lockId">The lock id given to <see cref="IAdvisoryLock.TryAttainLockAsync" /></param>
    /// <param name="token">
    /// Bounds only the wait for the lock's own connection. Cancelling it never reads as a lost lock.
    /// </param>
    Task<bool> HasLockAsync(int lockId, CancellationToken token);
}
