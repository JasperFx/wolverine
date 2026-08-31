using JasperFx.Core.Reflection;
using StackExchange.Redis;

namespace Wolverine.Redis.Internal;

/// <summary>
/// Public because Wolverine generates code that constructs it directly; an internal type would force
/// service location, which ServiceLocationPolicy.NotAllowed refuses.
/// </summary>
/// <remarks>
/// StackExchange.Redis takes no <see cref="CancellationToken" /> on any of its async methods — its
/// timeouts are configured on the multiplexer. The tokens here are honoured at the entry of each call
/// so that a handler cancelled before it reaches Redis does not make the round trip, which is the whole
/// of what can honestly be done.
/// </remarks>
public class RedisDocumentSession : IRedisDocumentSession
{
    private readonly RedisPersistenceConfiguration _configuration;
    private readonly IConnectionMultiplexer _multiplexer;

    public RedisDocumentSession(IConnectionMultiplexer multiplexer, RedisPersistenceConfiguration configuration)
    {
        _multiplexer = multiplexer;
        _configuration = configuration;
    }

    public async Task<T?> LoadAsync<T>(object id, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        token.ThrowIfCancellationRequested();

        var mapping = _configuration.MappingFor(typeof(T));
        var key = mapping.KeyForIdentity(id, tenantId);

        // A saga lives in a hash so that its revision and its state are one key; a document is a plain
        // string, which is both cheaper and what anything else reading this Redis would expect. Reading
        // one as the other is a WRONGTYPE error, so the shape follows the registration on every path,
        // not just the saga chain's own.
        var value = mapping.IsSaga
            ? await databaseFor(mapping).HashGetAsync(key, RedisSagaScripts.DataField).ConfigureAwait(false)
            : await databaseFor(mapping).StringGetAsync(key).ConfigureAwait(false);

        return value.IsNull ? null : mapping.Serializer.Deserialize<T>((byte[])value!);
    }

    public Task StoreAsync<T>(T document, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);
        token.ThrowIfCancellationRequested();

        var mapping = _configuration.MappingFor(typeof(T));
        var body = mapping.Serializer.Serialize(document).ToArray();

        if (mapping.IsSaga)
        {
            return evaluateAsync(mapping, RedisSagaScripts.BlindWrite,
                mapping.KeyForEntity(document, tenantId), [body, expiryIn(mapping)]);
        }

        return databaseFor(mapping).StringSetAsync(mapping.KeyForEntity(document, tenantId), body,
            mapping.Expiry);
    }

    public Task DeleteAsync<T>(T document, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);
        token.ThrowIfCancellationRequested();

        var mapping = _configuration.MappingFor(typeof(T));

        // DEL reports 0 for a key that was never there, so this is naturally idempotent.
        return databaseFor(mapping).KeyDeleteAsync(mapping.KeyForEntity(document, tenantId));
    }

    public Task DeleteByIdAsync<T>(object id, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        token.ThrowIfCancellationRequested();

        var mapping = _configuration.MappingFor(typeof(T));

        return databaseFor(mapping).KeyDeleteAsync(mapping.KeyForIdentity(id, tenantId));
    }

    public async Task<RedisSagaState<T>> LoadSagaAsync<T>(object id, string? tenantId,
        CancellationToken token = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        token.ThrowIfCancellationRequested();

        var mapping = _configuration.MappingFor(typeof(T));

        // One HMGET rather than two reads, so the revision and the state can never come from different
        // revisions of the saga.
        var fields = await databaseFor(mapping).HashGetAsync(mapping.KeyForIdentity(id, tenantId),
            [RedisSagaScripts.VersionField, RedisSagaScripts.DataField]).ConfigureAwait(false);

        if (fields[0].IsNull || fields[1].IsNull)
        {
            return new RedisSagaState<T>(null, null);
        }

        return new RedisSagaState<T>(mapping.Serializer.Deserialize<T>((byte[])fields[1]!), fields[0].ToString());
    }

    public async Task InsertSagaAsync<T>(T saga, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(saga);
        token.ThrowIfCancellationRequested();

        var mapping = _configuration.MappingFor(typeof(T));
        var key = mapping.KeyForEntity(saga, tenantId);

        var result = await evaluateAsync(mapping, RedisSagaScripts.Insert, key,
            [mapping.Serializer.Serialize(saga).ToArray(), expiryIn(mapping)]).ConfigureAwait(false);

        if (result != RedisSagaScripts.Applied)
        {
            throw new SagaConcurrencyException(
                $"Saga of type {typeof(T).FullNameInCode()} and id {key} could not be started because one already exists at that key");
        }
    }

    public async Task UpdateSagaAsync<T>(T saga, string? version, string? tenantId,
        CancellationToken token = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(saga);
        token.ThrowIfCancellationRequested();

        var mapping = _configuration.MappingFor(typeof(T));
        var key = mapping.KeyForEntity(saga, tenantId);

        var result = await evaluateAsync(mapping, RedisSagaScripts.Update, key,
            [version ?? string.Empty, mapping.Serializer.Serialize(saga).ToArray(), expiryIn(mapping)])
            .ConfigureAwait(false);

        if (result == RedisSagaScripts.Applied)
        {
            return;
        }

        // A vanished saga is reported as a concurrency violation rather than quietly re-created:
        // another message completed this saga while this one was in flight, and writing the state back
        // would resurrect a saga that is meant to be over.
        throw new SagaConcurrencyException(result == RedisSagaScripts.Missing
            ? $"Saga of type {typeof(T).FullNameInCode()} and id {key} cannot be updated because it was completed by another message"
            : $"Saga of type {typeof(T).FullNameInCode()} and id {key} cannot be updated because of optimistic concurrency violations");
    }

    public async Task DeleteSagaAsync<T>(object id, string? version, string? tenantId,
        CancellationToken token = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        token.ThrowIfCancellationRequested();

        var mapping = _configuration.MappingFor(typeof(T));
        var key = mapping.KeyForIdentity(id, tenantId);

        var result = await evaluateAsync(mapping, RedisSagaScripts.Delete, key, [version ?? string.Empty])
            .ConfigureAwait(false);

        // Missing is the one outcome that is NOT a violation here. The saga is gone, which is exactly
        // what completing it was asking for; another message simply got there first.
        if (result == RedisSagaScripts.VersionMismatch)
        {
            throw new SagaConcurrencyException(
                $"Saga of type {typeof(T).FullNameInCode()} and id {key} cannot be completed because of optimistic concurrency violations");
        }
    }

    private async Task<long> evaluateAsync(RedisDocumentMapping mapping, string script, RedisKey key,
        RedisValue[] values)
    {
        var result = await databaseFor(mapping).ScriptEvaluateAsync(script, [key], values).ConfigureAwait(false);

        return (long)result;
    }

    private static RedisValue expiryIn(RedisDocumentMapping mapping)
    {
        return mapping.Expiry is { } expiry ? (long)expiry.TotalMilliseconds : 0L;
    }

    private IDatabase databaseFor(RedisDocumentMapping mapping)
    {
        return _multiplexer.GetDatabase(mapping.Database);
    }
}
