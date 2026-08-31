using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Sagas;
using Wolverine.Redis.Internal;

namespace Wolverine.Redis;

public static class WolverineRedisPersistenceExtensions
{
    /// <summary>
    /// Keep the named document and saga types in Redis, so a plain <c>[Entity]</c> parameter, the
    /// declarative <c>Storage.Store()</c> / <c>Storage.Delete()</c> return values, and saga state all
    /// resolve against Redis keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires an <see cref="StackExchange.Redis.IConnectionMultiplexer" /> in the service container,
    /// left to the application so it keeps its own connection, authentication and reconnect policy. It
    /// is deliberately not taken from the Redis <em>transport</em>: the transport owns its multiplexer's
    /// lifetime, is often pointed at a different Redis than the application's data, and does not have to
    /// be configured at all for this to be used.
    /// </para>
    /// <para>
    /// This does not make Redis the message store. The transactional inbox and outbox stay with
    /// whichever database the application uses.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// opts.UseRedisPersistence(redis =&gt;
    /// {
    ///     redis.Store&lt;ShippingQuote&gt;(x =&gt;
    ///     {
    ///         x.KeyFor = ctx =&gt; $"quote:{ctx.TenantId}:{ctx.Id}";
    ///         x.ExpiresAfter = 30.Minutes();
    ///     });
    ///
    ///     redis.Saga&lt;OrderSaga&gt;(x =&gt; x.KeyFor = ctx =&gt; $"saga:order:{ctx.Id}");
    /// });
    /// </code>
    /// </example>
    public static WolverineOptions UseRedisPersistence(this WolverineOptions options,
        Action<RedisPersistenceConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var configuration = new RedisPersistenceConfiguration();
        configure(configuration);

        // Read back by RedisPersistenceFrameProvider when the frames are generated.
        options.Services.AddSingleton(configuration);

        options.Services.AddScoped<IRedisDocumentSession, RedisDocumentSession>();
        options.Services.AddSingleton<IHostedService, RedisPersistenceStartupValidator>();

        options.CodeGeneration.InsertFirstPersistenceStrategy<RedisPersistenceFrameProvider>();

        // Without this the generated code does not compile: it references RedisStorageActionApplier.
        options.CodeGeneration.ReferenceAssembly(typeof(WolverineRedisPersistenceExtensions).Assembly);

        return options;
    }
}
