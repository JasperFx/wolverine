namespace Wolverine.Redis.Internal;

/// <summary>
/// The compare-and-swap primitive behind Redis saga storage.
/// </summary>
/// <remarks>
/// <para>
/// A saga is one Redis hash with two fields: <c>v</c>, a revision counter, and <c>d</c>, the serialized
/// state. Every write is a Lua script that re-reads <c>v</c> and refuses unless it still matches the
/// revision the message read. Redis runs a script to completion before it will run anything else, so
/// the read-compare-write is genuinely atomic without any locking.
/// </para>
/// <para>
/// Lua rather than <c>WATCH</c>/<c>MULTI</c>/<c>EXEC</c> deliberately. StackExchange.Redis multiplexes
/// every caller onto shared connections, and <c>WATCH</c> is connection state — the library will not
/// expose it directly, and its <c>ITransaction</c> conditions have to reserve a connection out of the
/// pool to hold that state for the duration. A script is one round trip, holds nothing, and cannot be
/// left dangling by a handler that throws between the read and the write.
/// </para>
/// <para>
/// One key per script, so this is Redis Cluster safe: every key a script touches hashes to the same
/// slot because there is only one.
/// </para>
/// </remarks>
internal static class RedisSagaScripts
{
    /// <summary>The write happened.</summary>
    public const long Applied = 1;

    /// <summary>The stored revision was not the one the caller read: somebody else got there first.</summary>
    public const long VersionMismatch = 0;

    /// <summary>There is no saga at that key at all any more.</summary>
    public const long Missing = -1;

    /// <summary>
    /// KEYS[1] = saga key. ARGV[1] = state. ARGV[2] = TTL in milliseconds, or 0 for none.
    /// <para>
    /// Create-if-absent. The <c>EXISTS</c> guard is the multi-field equivalent of <c>SET NX</c>: a second
    /// node that starts the same saga concurrently loses here rather than overwriting the first one's
    /// state with an empty saga.
    /// </para>
    /// </summary>
    public const string Insert =
        """
        if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
        redis.call('HSET', KEYS[1], 'v', '1', 'd', ARGV[1])
        if tonumber(ARGV[2]) > 0 then redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
        return 1
        """;

    /// <summary>
    /// KEYS[1] = saga key. ARGV[1] = expected revision. ARGV[2] = new state. ARGV[3] = TTL in
    /// milliseconds, or 0 for none.
    /// <para>
    /// A missing key is reported separately from a mismatched revision: it means another message
    /// completed this saga while this one was in flight, and rewriting the state would resurrect a saga
    /// that is supposed to be finished.
    /// </para>
    /// </summary>
    public const string Update =
        """
        local current = redis.call('HGET', KEYS[1], 'v')
        if current == false then return -1 end
        if current ~= ARGV[1] then return 0 end
        redis.call('HSET', KEYS[1], 'd', ARGV[2])
        redis.call('HINCRBY', KEYS[1], 'v', 1)
        if tonumber(ARGV[3]) > 0 then redis.call('PEXPIRE', KEYS[1], ARGV[3]) end
        return 1
        """;

    /// <summary>
    /// KEYS[1] = saga key. ARGV[1] = expected revision.
    /// <para>
    /// Completing a saga is as destructive as updating it — a blind delete would drop a concurrent
    /// write that landed after this message read the saga — so the delete is compare-and-swap too.
    /// </para>
    /// </summary>
    public const string Delete =
        """
        local current = redis.call('HGET', KEYS[1], 'v')
        if current == false then return -1 end
        if current ~= ARGV[1] then return 0 end
        redis.call('DEL', KEYS[1])
        return 1
        """;

    /// <summary>
    /// KEYS[1] = saga key. ARGV[1] = new state. ARGV[2] = TTL in milliseconds, or 0 for none.
    /// <para>
    /// The escape hatch: a saga written through an explicit <c>Storage.Store()</c> side effect rather
    /// than through the saga chain, where there is no revision to compare against. Still has to write
    /// the hash shape the saga loader reads back, and still bumps the revision so that any message
    /// mid-flight over this saga loses its own compare-and-swap rather than silently overwriting this.
    /// </para>
    /// </summary>
    public const string BlindWrite =
        """
        redis.call('HSET', KEYS[1], 'd', ARGV[1])
        redis.call('HINCRBY', KEYS[1], 'v', 1)
        if tonumber(ARGV[2]) > 0 then redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
        return 1
        """;

    /// <summary>The revision field of the saga hash.</summary>
    public const string VersionField = "v";

    /// <summary>The serialized state field of the saga hash.</summary>
    public const string DataField = "d";
}
