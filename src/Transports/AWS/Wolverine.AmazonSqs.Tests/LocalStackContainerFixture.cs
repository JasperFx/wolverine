using System.Runtime.CompilerServices;
using Testcontainers.LocalStack;

namespace Wolverine.AmazonSqs.Tests;

public static class LocalStackContainerFixture
{
    private static LocalStackContainer? _container;

    public static int Port { get; private set; } = 4566;

    [ModuleInitializer]
    internal static void Initialize()
    {
        _container = new LocalStackBuilder()
            .WithImage("localstack/localstack:latest")
            .Build();

        _container.StartAsync().GetAwaiter().GetResult();
        Port = _container.GetMappedPublicPort(4566);
    }
}
