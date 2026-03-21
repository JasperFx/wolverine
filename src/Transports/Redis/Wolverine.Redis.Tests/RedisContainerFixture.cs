using System.Runtime.CompilerServices;
using Testcontainers.Redis;

namespace Wolverine.Redis.Tests;

public static class RedisContainerFixture
{
    private static RedisContainer? _container;

    public static string ConnectionString { get; private set; } = "localhost:6379";

    [ModuleInitializer]
    internal static void Initialize()
    {
        _container = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();

        _container.StartAsync().GetAwaiter().GetResult();
        ConnectionString = _container.GetConnectionString();
    }
}
