using System.Collections.Concurrent;
using System.Linq;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Redis.Internal;

public class RedisTransport : BrokerTransport<RedisStreamEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "redis";
    
    private readonly LightweightCache<string, RedisStreamEndpoint> _streams;
    private readonly ConcurrentDictionary<string, IConnectionMultiplexer> _connections = new();
    private readonly Lazy<IConnectionMultiplexer> _defaultConnection;

    // Exactly one of these four connection sources is populated, used in precedence order: a
    // caller-managed multiplexer, a factory that resolves one from the IoC container, caller-supplied
    // ConfigurationOptions, or a connection string. GH-3110 — the first three let callers wire up
    // StackExchange.Redis extensions such as Microsoft.Azure.StackExchangeRedis for Entra ID /
    // Managed Identity token refresh. Wolverine owns (and disposes) only the multiplexers it builds
    // itself from ConfigurationOptions / a connection string.
    private readonly IConnectionMultiplexer? _externalConnection;
    private readonly Func<IServiceProvider, IConnectionMultiplexer>? _connectionFactory;
    private IServiceProvider? _services;
    private readonly ConfigurationOptions? _configurationOptions;
    private readonly string? _connectionString;

    /// <summary>
    /// Enable/disable creation of system endpoints like reply streams
    /// </summary>
    public bool SystemQueuesEnabled { get; set; } = true;
    
    public bool DeleteStreamEntryOnAck { get; set; } = false;

    /// <summary>
    /// Database ID to use for the per-node reply stream endpoint. Defaults to 0.
    /// </summary>
    public int ReplyDatabaseId { get; set; } = 0;

    /// <summary>
    /// Customizable selector to build a stable consumer name for listeners when an endpoint-level ConsumerName is not set.
    /// Defaults to ServiceName-NodeNumber-MachineName (lowercased and sanitized).
    /// </summary>
    [DescribeAsConfigurationState]
    public Func<IWolverineRuntime, RedisStreamEndpoint, string>? DefaultConsumerNameSelector { get; set; }
    
    /// <summary>
    /// Default constructor for GetOrCreate pattern - uses localhost:6379
    /// </summary>
    public RedisTransport() : this("localhost:6379")
    {
        // Default constructor for GetOrCreate<T>()
    }
    
    /// <summary>
    /// Connect to Redis with a StackExchange.Redis connection string. Wolverine owns the underlying
    /// <see cref="ConnectionMultiplexer"/> and disposes it on shutdown.
    /// </summary>
    public RedisTransport(string connectionString) : base(ProtocolName, "Redis Streams Transport", ["redis"])
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _streams = buildStreamCache();
        _defaultConnection = new Lazy<IConnectionMultiplexer>(createDefaultConnection);
    }

    /// <summary>
    /// Connect to Redis with a caller-supplied <see cref="ConfigurationOptions"/>. Wolverine owns the
    /// <see cref="ConnectionMultiplexer"/> it builds from these options and disposes it on shutdown. Use
    /// this to wire up StackExchange.Redis extensions (e.g. Microsoft.Azure.StackExchangeRedis for Entra
    /// ID / Managed Identity token refresh) that augment <see cref="ConfigurationOptions"/>. GH-3110.
    /// </summary>
    public RedisTransport(ConfigurationOptions configurationOptions) : base(ProtocolName, "Redis Streams Transport", ["redis"])
    {
        _configurationOptions = configurationOptions ?? throw new ArgumentNullException(nameof(configurationOptions));
        _streams = buildStreamCache();
        _defaultConnection = new Lazy<IConnectionMultiplexer>(createDefaultConnection);
    }

    /// <summary>
    /// Connect to Redis with a caller-managed <see cref="IConnectionMultiplexer"/>. Wolverine uses the
    /// supplied multiplexer as-is and does NOT dispose it — the caller owns its lifetime (and any token
    /// refresh / reconnect policy wired into it). GH-3110.
    /// </summary>
    public RedisTransport(IConnectionMultiplexer connection) : base(ProtocolName, "Redis Streams Transport", ["redis"])
    {
        _externalConnection = connection ?? throw new ArgumentNullException(nameof(connection));
        _streams = buildStreamCache();
        _defaultConnection = new Lazy<IConnectionMultiplexer>(createDefaultConnection);
    }

    /// <summary>
    /// Connect to Redis with an <see cref="IConnectionMultiplexer"/> resolved from the application's IoC
    /// container at runtime. Use this to share a single multiplexer (e.g. one registered as a singleton,
    /// possibly with Microsoft.Azure.StackExchangeRedis token refresh) between Wolverine and the rest of
    /// the application. The resolved multiplexer is assumed to be owned by the container — Wolverine uses
    /// it as-is and does NOT dispose it. GH-3110.
    /// </summary>
    public RedisTransport(Func<IServiceProvider, IConnectionMultiplexer> connectionFactory) : base(ProtocolName, "Redis Streams Transport", ["redis"])
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _streams = buildStreamCache();
        _defaultConnection = new Lazy<IConnectionMultiplexer>(createDefaultConnection);
    }

    private LightweightCache<string, RedisStreamEndpoint> buildStreamCache()
    {
        return new LightweightCache<string, RedisStreamEndpoint>(
            cacheKey =>
            {
                // Parse the cache key format: {databaseId}:{streamKey}
                var parts = cacheKey.Split(':', 2);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var databaseId))
                {
                    throw new ArgumentException($"Invalid cache key format. Expected 'databaseId:streamKey', got '{cacheKey}'");
                }
                var streamKey = parts[1];
                return new RedisStreamEndpoint(
                    new Uri($"{ProtocolName}://stream/{databaseId}/{streamKey}"),
                    this,
                    EndpointRole.Application);
            });
    }

    private IConnectionMultiplexer createDefaultConnection()
    {
        if (_externalConnection != null) return _externalConnection;

        if (_connectionFactory != null)
        {
            var services = _services ?? throw new InvalidOperationException(
                "The Redis transport's IConnectionMultiplexer factory cannot be resolved before the Wolverine host has started. " +
                "This usually means a Redis connection was requested before ConnectAsync ran.");
            return _connectionFactory(services);
        }

        if (_configurationOptions != null) return ConnectionMultiplexer.Connect(_configurationOptions);
        return ConnectionMultiplexer.Connect(_connectionString!);
    }

    /// <summary>
    /// True when Wolverine built the default connection itself (from a connection string or
    /// ConfigurationOptions) and is therefore responsible for disposing it. A caller-managed multiplexer
    /// or one resolved from the IoC container is owned elsewhere and must not be disposed here.
    /// </summary>
    private bool OwnsDefaultConnection => _externalConnection == null && _connectionFactory == null;

    public override Uri ResourceUri
    {
        get
        {
            // Derive the resource URI from whichever connection source is configured.
            var endpoint = primaryEndPoint();

            if (endpoint == null)
            {
                return new Uri($"{ProtocolName}://localhost:6379");
            }

            return new Uri($"{ProtocolName}://{endpoint}");
        }
    }

    private System.Net.EndPoint? primaryEndPoint()
    {
        if (_externalConnection != null) return _externalConnection.GetEndPoints().FirstOrDefault();
        if (_connectionFactory != null)
        {
            // Only known once the factory has resolved a multiplexer (after the host has started).
            return _defaultConnection.IsValueCreated ? _defaultConnection.Value.GetEndPoints().FirstOrDefault() : null;
        }
        if (_configurationOptions != null) return _configurationOptions.EndPoints.FirstOrDefault();
        return ConfigurationOptions.Parse(_connectionString!).EndPoints.FirstOrDefault();
    }

    /// <summary>
    /// A diagnostic-safe summary of how this transport connects to Redis. The <c>password</c> in a
    /// connection string or <see cref="ConfigurationOptions"/> is masked; a caller-managed multiplexer is
    /// reported as such (Wolverine never sees its credentials). Safe to render in diagnostic output.
    /// </summary>
    public string ConnectionSummary =>
        _externalConnection != null ? "caller-managed IConnectionMultiplexer"
        : _connectionFactory != null ? "caller-managed IConnectionMultiplexer factory"
        : _configurationOptions != null ? SanitizeConfigurationOptionsForLogging(_configurationOptions)
        : SanitizeConnectionStringForLogging(_connectionString!);

    internal IDatabase GetDatabase(string? connectionString = null, int database = 0)
    {
        var connection = GetConnection(connectionString);
        return connection.GetDatabase(database);
    }

    internal IConnectionMultiplexer GetConnection(string? connectionString = null)
    {
        // A caller-managed multiplexer is the single shared connection, regardless of any per-endpoint
        // connection-string override.
        if (_externalConnection != null) return _externalConnection;

        if (connectionString != null)
        {
            return _connections.GetOrAdd(connectionString, cs => ConnectionMultiplexer.Connect(cs));
        }

        return _defaultConnection.Value;
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        // Capture the IoC container so a factory-based connection can be resolved. ConnectAsync runs
        // before any endpoint forces a connection (BrokerTransport.startupAsync), so this is set in time.
        _services ??= runtime.Services;

        runtime.Logger.LogInformation("Connecting to Redis at {ConnectionString}", ConnectionSummary);
        
        try
        {
            // Initialize the default connection
            var connection = GetConnection();
            
            // Test the connection
            var db = connection.GetDatabase();
            await db.PingAsync();
            
            runtime.Logger.LogInformation("Successfully connected to Redis");
        }
        catch (Exception ex)
        {
            runtime.Logger.LogError(ex, "Failed to connect to Redis");
            throw;
        }
    }

    public WolverineTransportHealthCheck BuildHealthCheck(IWolverineRuntime runtime)
    {
        return new RedisHealthCheck(this);
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Stream Key", "streamKey");
        yield return new PropertyColumn("Consumer Group", "consumerGroup"); 
        yield return new PropertyColumn("Message Count", "messageCount", Justify.Right);
    }

    protected override IEnumerable<RedisStreamEndpoint> endpoints()
    {
        return _streams.ToArray();
    }

    protected override RedisStreamEndpoint findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != ProtocolName)
        {
            throw new ArgumentException($"Invalid scheme for Redis transport: {uri.Scheme}");
        }

        // Only support the new format: redis://stream/{dbId}/{streamKey}
        if (!uri.Host.Equals("stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Redis URI must use the format 'redis://stream/{{dbId}}/{{streamKey}}': {uri}");
        }

        if (uri.Segments.Length < 3)
        {
            throw new ArgumentException($"Redis URI must specify both database ID and stream key in format 'redis://stream/{{dbId}}/{{streamKey}}': {uri}");
        }

        var databaseSegment = uri.Segments[1].TrimEnd('/');
        var streamKeySegment = uri.Segments[2].TrimEnd('/');

        if (!int.TryParse(databaseSegment, out var databaseId) || databaseId < 0)
        {
            throw new ArgumentException($"Database ID must be a non-negative integer in Redis URI: {uri}");
        }

        if (string.IsNullOrEmpty(streamKeySegment))
        {
            throw new ArgumentException($"Stream key cannot be empty in Redis URI: {uri}");
        }

        // Create cache key that includes database ID to avoid conflicts
        var cacheKey = $"{databaseId}:{streamKeySegment}";
        var endpoint = _streams[cacheKey];

        // Parse consumer group from query string if present
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var consumerGroup = query["consumerGroup"];
        if (!string.IsNullOrEmpty(consumerGroup))
        {
            endpoint.ConsumerGroup = consumerGroup;
        }

        return endpoint;
    }

    /// <summary>
    /// Get or create a Redis stream endpoint by stream key (uses database 0)
    /// </summary>
    public RedisStreamEndpoint StreamEndpoint(string streamKey)
    {
        return StreamEndpoint(streamKey, 0);
    }

    /// <summary>
    /// Get or create a Redis stream endpoint by stream key and database ID
    /// </summary>
    public RedisStreamEndpoint StreamEndpoint(string streamKey, int databaseId)
    {
        var cacheKey = $"{databaseId}:{streamKey}";
        return _streams[cacheKey];
    }

    /// <summary>
    /// Configure a Redis stream endpoint (uses database 0)
    /// </summary>
    public RedisStreamEndpoint StreamEndpoint(string streamKey, Action<RedisStreamEndpoint> configure)
    {
        return StreamEndpoint(streamKey, 0, configure);
    }

    /// <summary>
    /// Configure a Redis stream endpoint with database ID
    /// </summary>
    public RedisStreamEndpoint StreamEndpoint(string streamKey, int databaseId, Action<RedisStreamEndpoint> configure)
    {
        var endpoint = StreamEndpoint(streamKey, databaseId);
        configure(endpoint);
        return endpoint;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var owned = _connections.Values.ToList();

            // Dispose the default connection only if Wolverine created it. A caller-managed
            // IConnectionMultiplexer, or one resolved from the IoC container via a factory, is owned
            // elsewhere and must never be disposed here. GH-3110.
            if (_defaultConnection.IsValueCreated && OwnsDefaultConnection)
            {
                owned.Add(_defaultConnection.Value);
            }

            foreach (var connection in owned.Distinct())
            {
                await connection.CloseAsync();
                connection.Dispose();
            }
            _connections.Clear();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }
        catch (Exception ex)
        {
            // Log but don't throw during disposal
            Console.WriteLine($"Error during Redis connection disposal: {ex.Message}");
        }
    }

    internal string ComputeConsumerName(IWolverineRuntime runtime, RedisStreamEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.ConsumerName)) return endpoint.ConsumerName!;
        var selector = DefaultConsumerNameSelector ?? BuildDefaultConsumerName;
        var name = selector(runtime, endpoint);
        return SanitizeName(name);
    }

    private static string BuildDefaultConsumerName(IWolverineRuntime runtime, RedisStreamEndpoint endpoint)
    {
        var service = string.IsNullOrWhiteSpace(runtime.Options.ServiceName)
            ? "wolverine"
            : runtime.Options.ServiceName!.Trim();
        var node = runtime.DurabilitySettings.AssignedNodeNumber.ToString();
        var host = Environment.MachineName;
        var name = $"{service}-{node}-{host}";
        return name.ToLowerInvariant();
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "wolverine";
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ':' || ch == '.' ? ch : '-').ToArray();
        var sanitized = new string(chars);
        return sanitized.Trim('-');
    }

    private static string SanitizeConnectionStringForLogging(string connectionString)
    {
        // Remove password from connection string for logging
        var options = ConfigurationOptions.Parse(connectionString);
        options.Password = options.Password?.Length > 0 ? "****" : null;
        return options.ToString();
    }

    private static string SanitizeConfigurationOptionsForLogging(ConfigurationOptions options)
    {
        // Mask the password without mutating the caller's options instance.
        var clone = options.Clone();
        clone.Password = clone.Password?.Length > 0 ? "****" : null;
        return clone.ToString();
    }

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
        if (!SystemQueuesEnabled) return;

        // Create a per-node reply stream endpoint similar to other transports (using database 0).
        // In Solo mode the assigned node number is always 1 (#3188) and the service name is not
        // unique per host, so multiple Solo hosts on one Redis would share a reply stream and
        // cross-deliver replies — key on the always-unique UniqueNodeId instead. Balanced gets a
        // unique AssignedNodeNumber via election. See #3189.
        var replyNode = runtime.Options.Durability.Mode == DurabilityMode.Solo
            ? runtime.Options.UniqueNodeId.ToString("N")
            : runtime.DurabilitySettings.AssignedNodeNumber.ToString();
        var replyStreamKey = $"wolverine.response.{runtime.Options.ServiceName}.{replyNode}".ToLowerInvariant();
        var cacheKey = $"{ReplyDatabaseId}:{replyStreamKey}";
        var replyEndpoint = new RedisStreamEndpoint(
            new Uri($"{ProtocolName}://stream/{ReplyDatabaseId}/{replyStreamKey}?consumerGroup=wolverine-replies"),
            this,
            EndpointRole.System)
        {
            IsListener = true,
            IsUsedForReplies = true,
            ConsumerGroup = "wolverine-replies",
            EndpointName = "RedisReplies"
        };

        _streams[cacheKey] = replyEndpoint;
    }
}
