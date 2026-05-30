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

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        _container.StartAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

        ConnectionString = _container.GetConnectionString();
    }
}
