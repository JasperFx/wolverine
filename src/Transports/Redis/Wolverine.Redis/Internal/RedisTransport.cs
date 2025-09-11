using System.Collections.Concurrent;
using System.Linq;
using JasperFx.Core;
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
    private readonly string _connectionString;

    /// <summary>
    /// Enable/disable creation of system endpoints like reply streams
    /// </summary>
    public bool SystemQueuesEnabled { get; set; } = true;

    /// <summary>
    /// Database ID to use for the per-node reply stream endpoint. Defaults to 0.
    /// </summary>
    public int ReplyDatabaseId { get; set; } = 0;

    /// <summary>
    /// Customizable selector to build a stable consumer name for listeners when an endpoint-level ConsumerName is not set.
    /// Defaults to ServiceName-NodeNumber-MachineName (lowercased and sanitized).
    /// </summary>
    public Func<IWolverineRuntime, RedisStreamEndpoint, string>? DefaultConsumerNameSelector { get; set; }
    
    /// <summary>
    /// Default constructor for GetOrCreate pattern - uses localhost:6379
    /// </summary>
    public RedisTransport() : this("localhost:6379")
    {
        // Default constructor for GetOrCreate<T>()
    }
    
    public RedisTransport(string connectionString) : base(ProtocolName, "Redis Streams Transport")
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _streams = new LightweightCache<string, RedisStreamEndpoint>(
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

    public override Uri ResourceUri
    {
        get
        {
            // Parse connection string to build resource URI
            var options = ConfigurationOptions.Parse(_connectionString);
            var endpoint = options.EndPoints.FirstOrDefault();
            
            if (endpoint == null)
            {
                return new Uri($"{ProtocolName}://localhost:6379");
            }
            
            return new Uri($"{ProtocolName}://{endpoint}");
        }
    }

    internal IDatabase GetDatabase(string? connectionString = null, int database = 0)
    {
        var connection = GetConnection(connectionString);
        return connection.GetDatabase(database);
    }
    
    internal IConnectionMultiplexer GetConnection(string? connectionString = null)
    {
        var connStr = connectionString ?? _connectionString;
        return _connections.GetOrAdd(connStr, cs => ConnectionMultiplexer.Connect(cs));
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        runtime.Logger.LogInformation("Connecting to Redis at {ConnectionString}", 
            SanitizeConnectionStringForLogging(_connectionString));
        
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
            foreach (var connection in _connections.Values)
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

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
        if (!SystemQueuesEnabled) return;

        // Create a per-node reply stream endpoint similar to other transports (using database 0)
        var replyStreamKey = $"wolverine.response.{runtime.Options.ServiceName}.{runtime.DurabilitySettings.AssignedNodeNumber}".ToLowerInvariant();
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
