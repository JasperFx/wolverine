using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Wolverine.Redis.Internal;

/// <summary>
/// Refuses to start an application whose Redis persistence registration cannot work, and reports a Redis
/// deployed as a cache rather than as a store.
/// </summary>
/// <remarks>
/// <para>
/// The eviction check exists because the failure it catches is invisible. A Redis running
/// <c>maxmemory-policy allkeys-lru</c> will evict a live saga to make room for something else, at which
/// point the next message for that saga either starts a second one or throws
/// <c>UnknownSagaException</c> — with nothing anywhere saying why. Nothing in the client can detect that
/// after the fact; the key is simply gone. Asking the server how it is configured, once, at startup, is
/// the only place the question can be answered at all.
/// </para>
/// <para>
/// It is a warning rather than a failure by default because the answer is not always available: managed
/// Redis offerings routinely block <c>CONFIG</c> outright (Azure Cache for Redis), and a probe that
/// cannot read the setting must not be the reason a deployment fails. Set
/// <see cref="RedisPersistenceConfiguration.DurabilityCheck" /> to
/// <see cref="RedisDurabilityCheck.Throw" /> where losing a saga is worse than failing to deploy.
/// </para>
/// </remarks>
internal class RedisPersistenceStartupValidator : IHostedService
{
    private readonly RedisPersistenceConfiguration _configuration;
    private readonly ILogger<RedisPersistenceStartupValidator> _logger;
    private readonly IServiceProvider _services;

    public RedisPersistenceStartupValidator(RedisPersistenceConfiguration configuration, IServiceProvider services,
        ILogger<RedisPersistenceStartupValidator> logger)
    {
        _configuration = configuration;
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_configuration.Mappings.Count == 0)
        {
            _logger.LogWarning(
                "UseRedisPersistence() was called but no types were registered with Store<T>() or Saga<T>(), so nothing will resolve to Redis");

            return;
        }

        var multiplexer = _services.GetService<IConnectionMultiplexer>();
        if (multiplexer == null)
        {
            throw new InvalidOperationException(
                $"No IConnectionMultiplexer is registered, but {_configuration.Mappings.Count} type(s) are registered to be stored in Redis. Register one before UseRedisPersistence() -- services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(...)).");
        }

        if (_configuration.DurabilityCheck == RedisDurabilityCheck.Disabled || !multiplexer.IsConnected)
        {
            return;
        }

        await assertServerIsNotJustACacheAsync(multiplexer).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task assertServerIsNotJustACacheAsync(IConnectionMultiplexer multiplexer)
    {
        var database = multiplexer.GetDatabase();
        var problems = new List<string>();

        var policy = await configurationValueAsync(database, "maxmemory-policy").ConfigureAwait(false);

        // Only the allkeys-* policies can take a key that was never given a TTL, which is what every
        // Wolverine document and saga is unless the mapping asked for one.
        if (policy?.StartsWith("allkeys-", StringComparison.OrdinalIgnoreCase) == true)
        {
            problems.Add(
                $"maxmemory-policy is '{policy}', which evicts ANY key under memory pressure -- including live saga state, silently and with no error anywhere. Use a 'volatile-*' policy (which only evicts keys that were given a TTL) or 'noeviction'.");
        }

        // An empty 'save' means no RDB snapshots are scheduled. Together with the append-only log being
        // off, that is a server that keeps nothing across a restart. Either one alone is a perfectly
        // ordinary way to run Redis, so both have to be true before this says anything.
        var appendOnly = await configurationValueAsync(database, "appendonly").ConfigureAwait(false);
        var save = await configurationValueAsync(database, "save").ConfigureAwait(false);

        if (appendOnly?.Equals("no", StringComparison.OrdinalIgnoreCase) == true && save == string.Empty)
        {
            problems.Add(
                "there is no persistence configured (appendonly is off and no RDB save points are set), so everything Wolverine writes here is lost when the server restarts.");
        }

        if (problems.Count == 0)
        {
            return;
        }

        var sagas = _configuration.Mappings.Count(x => x.IsSaga);
        var message =
            $"This Redis is configured as a cache, not as a store, and Wolverine is keeping {_configuration.Mappings.Count} type(s) in it" +
            (sagas > 0 ? $" including {sagas} saga type(s)" : string.Empty) + ":" +
            Environment.NewLine + string.Join(Environment.NewLine, problems.Select(x => "  - " + x)) +
            Environment.NewLine +
            $"Fix the server configuration, or set DurabilityCheck = RedisDurabilityCheck.{nameof(RedisDurabilityCheck.Disabled)} inside UseRedisPersistence() to accept it.";

        if (_configuration.DurabilityCheck == RedisDurabilityCheck.Throw)
        {
            throw new InvalidOperationException(message);
        }

        _logger.LogWarning("{Message}", message);
    }

    /// <summary>
    /// One server setting, or null when the server would not say.
    /// </summary>
    /// <remarks>
    /// Sent as a raw command through <see cref="IDatabase" /> rather than through
    /// <c>IServer.ConfigGetAsync</c>, and that is not a stylistic choice. StackExchange.Redis refuses
    /// every command on its <c>IServer</c> API — <c>CONFIG GET</c> and <c>INFO</c> alike — unless the
    /// multiplexer was built with <c>allowAdmin=true</c>, which almost no application does. A check
    /// written against that API would quietly do nothing for nearly every real deployment, which is
    /// worse than having no check at all, because it looks like one.
    /// </remarks>
    private async Task<string?> configurationValueAsync(IDatabase database, string name)
    {
        try
        {
            var result = await database.ExecuteAsync("CONFIG", "GET", name).ConfigureAwait(false);
            var pairs = (RedisResult[]?)result;

            return pairs is { Length: >= 2 } ? pairs[1].ToString() : null;
        }
        catch (Exception e)
        {
            // Managed Redis commonly blocks CONFIG outright. Not being allowed to ask is not the same
            // as a bad answer, and must never be the reason an application fails to start.
            _logger.LogDebug(e,
                "Could not read the Redis '{Setting}' setting to check the server is safe for Wolverine persistence",
                name);

            return null;
        }
    }
}
