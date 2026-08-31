using Xunit;
using IntegrationTests;
using StackExchange.Redis;

namespace Wolverine.Redis.Tests.Persistence;

/// <summary>
/// Where the persistence suites find Redis, and whether it is actually up.
/// </summary>
/// <remarks>
/// <para>
/// The address is <see cref="RedisContainerFixture.ConnectionString" /> — the same Redis the transport
/// suites in this assembly use, which is a Testcontainers instance unless <c>WOLVERINE_REDIS</c> points
/// somewhere else (say, <c>docker compose up -d redis-server</c> on localhost:6379).
/// </para>
/// <para>
/// The probe matters even so. A supplied <c>WOLVERINE_REDIS</c> is taken on trust by the fixture, which
/// starts no container for it, so a stale or wrong value would otherwise turn every test in these
/// suites into a connection-timeout failure rather than a skip. Same reason and same helper as the
/// object-store suites, and the same failure mode GH-4160 fixed there.
/// </para>
/// </remarks>
public static class RedisPersistenceServer
{
    public static string ConnectionString => RedisContainerFixture.ConnectionString;

    public const string SkipReason =
        "No Redis is reachable at the address in RedisContainerFixture.ConnectionString. " +
        "Start one with `docker compose up -d redis-server` from the repo root and set WOLVERINE_REDIS=localhost:6379, " +
        "or leave WOLVERINE_REDIS unset to let Testcontainers start one.";

    public static void SkipUnlessRunning()
    {
        Assert.SkipUnless(IsRunning, SkipReason);
    }

    public static bool IsRunning
    {
        get
        {
            var endpoint = ConnectionString.Split(',')[0];
            var parts = endpoint.Split(':');

            return parts.Length == 2 && int.TryParse(parts[1], out var port) &&
                   EmulatorProbe.IsListening(parts[0], port);
        }
    }

    /// <summary>
    /// Connect to the suite's Redis. <paramref name="allowAdmin" /> is what StackExchange.Redis requires
    /// before it will send any CONFIG command; the tests that reconfigure the server need it, and it is
    /// deliberately off by default so that the suites exercise the same permission level a normal
    /// application connects with.
    /// </summary>
    public static ConnectionMultiplexer Connect(bool allowAdmin = false)
    {
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AbortOnConnectFail = false;
        options.AllowAdmin = allowAdmin;

        return ConnectionMultiplexer.Connect(options);
    }

    /// <summary>
    /// A key prefix unique to one test, so suites that share a Redis instance — and repeat runs against
    /// a compose Redis that is never torn down — cannot see each other's keys.
    /// </summary>
    public static string UniquePrefix(string name)
    {
        return $"wolverine-test:{name}:{Guid.NewGuid():N}";
    }
}

/// <summary>
/// xUnit <see cref="FactAttribute" /> that skips when no Redis answers.
/// </summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!RedisPersistenceServer.IsRunning)
        {
            Skip = RedisPersistenceServer.SkipReason;
        }
    }
}
