using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Wolverine.Redis.Tests.Persistence;

/// <summary>
/// The startup probe that reports a Redis deployed as a cache.
/// </summary>
/// <remarks>
/// This is the check that has no runtime equivalent. An <c>allkeys-*</c> eviction policy will drop a
/// live saga to make room for something else, and nothing in any client can tell that apart from a saga
/// that was never there: the key is simply gone. Asking the server how it is configured, once, at
/// startup, is the only place the question can be asked at all.
/// </remarks>
public class redis_durability_check : IAsyncLifetime
{
    private ConnectionMultiplexer _admin = null!;
    private ConnectionMultiplexer _multiplexer = null!;
    private string? _originalPolicy;

    public ValueTask InitializeAsync()
    {
        if (RedisPersistenceServer.IsRunning)
        {
            // Two connections on purpose. The tests need CONFIG SET to move the server's eviction
            // policy, which StackExchange.Redis only allows on an admin connection -- but the
            // application under test connects the way a real one does, WITHOUT admin, which is the
            // whole point of reading the policy out of INFO rather than CONFIG GET.
            _admin = RedisPersistenceServer.Connect(true);
            _multiplexer = RedisPersistenceServer.Connect();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_originalPolicy != null)
        {
            await primary().ConfigSetAsync("maxmemory-policy", _originalPolicy);
        }

        _multiplexer?.Dispose();
        _admin?.Dispose();
    }

    private IServer primary()
    {
        return _admin.GetServers().First(x => x is { IsConnected: true, IsReplica: false });
    }

    private async Task useEvictionPolicyAsync(string policy)
    {
        var server = primary();
        _originalPolicy ??= (await server.ConfigGetAsync("maxmemory-policy"))[0].Value;

        await server.ConfigSetAsync("maxmemory-policy", policy);
    }

    private Task<IHost> buildHostAsync(RedisDurabilityCheck check)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(Recorder))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IConnectionMultiplexer>(_multiplexer);

                opts.UseRedisPersistence(redis =>
                {
                    redis.DurabilityCheck = check;
                    redis.Saga<GuardedSaga>(x => x.KeyFor = ctx => $"guarded:{ctx.Id}");
                });

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    public RecordingLoggerProvider Recorder { get; } = new();

    [RedisFact]
    public async Task warns_about_an_allkeys_eviction_policy_by_default()
    {
        await useEvictionPolicyAsync("allkeys-lru");

        using var host = await buildHostAsync(RedisDurabilityCheck.Warn);

        var warning = Recorder.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("allkeys-lru");
        warning.ShouldContain("evicts ANY key");
        warning.ShouldContain("1 saga type(s)");
    }

    [RedisFact]
    public async Task refuses_to_start_on_an_allkeys_policy_when_asked_to()
    {
        await useEvictionPolicyAsync("allkeys-lru");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            buildHostAsync(RedisDurabilityCheck.Throw));

        ex.Message.ShouldContain("configured as a cache, not as a store");
    }

    /// <summary>
    /// A "volatile-*" policy only evicts keys that were given a TTL, which is nothing Wolverine wrote
    /// unless the mapping asked for one. That is not a problem and must not be reported as one — a
    /// warning that fires on a healthy configuration is a warning nobody reads.
    /// </summary>
    [RedisFact]
    public async Task says_nothing_about_a_volatile_policy()
    {
        await useEvictionPolicyAsync("volatile-lru");

        using var host = await buildHostAsync(RedisDurabilityCheck.Warn);

        Recorder.Warnings.ShouldBeEmpty();
    }

    [RedisFact]
    public async Task the_check_can_be_turned_off_entirely()
    {
        await useEvictionPolicyAsync("allkeys-lru");

        using var host = await buildHostAsync(RedisDurabilityCheck.Disabled);

        Recorder.Warnings.ShouldBeEmpty();
    }

    /// <summary>
    /// The whole point of the mapping registration is that the key layout is the application's, so a
    /// registration with no multiplexer to talk to is a configuration error rather than something to
    /// discover inside the first handler that touches a document.
    /// </summary>
    [Fact]
    public async Task refuses_to_start_without_a_multiplexer()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRedisPersistence(redis =>
                    redis.Saga<GuardedSaga>(x => x.KeyFor = ctx => $"guarded:{ctx.Id}"));
                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync());

        ex.Message.ShouldContain("No IConnectionMultiplexer is registered");
    }
}

public class GuardedSaga : Saga
{
    public string Id { get; set; } = null!;
}

/// <summary>
/// Captures warnings so the startup check can be asserted on rather than eyeballed.
/// </summary>
public class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warnings)
            {
                return _warnings.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return categoryName.Contains("RedisPersistenceStartupValidator")
            ? new Recorder(this)
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public void Dispose()
    {
    }

    private void record(string message)
    {
        lock (_warnings)
        {
            _warnings.Add(message);
        }
    }

    private class Recorder : ILogger
    {
        private readonly RecordingLoggerProvider _parent;

        public Recorder(RecordingLoggerProvider parent)
        {
            _parent = parent;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                _parent.record(formatter(state, exception));
            }
        }
    }
}
