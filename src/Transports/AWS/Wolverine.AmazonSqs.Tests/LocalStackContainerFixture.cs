using System.Runtime.CompilerServices;
using Testcontainers.LocalStack;

namespace Wolverine.AmazonSqs.Tests;

public static class LocalStackContainerFixture
{
    private static LocalStackContainer? _container;

    public static int Port { get; private set; } = 4566;

    public static string ConnectionString { get; private set; } = "http://localhost:4566";

    [ModuleInitializer]
    internal static void Initialize()
    {
        _container = new LocalStackBuilder()
            .WithImage("localstack/localstack:4")
            .WithEnvironment("SERVICES", "sqs,sns")
            .Build();

        _container.StartAsync().GetAwaiter().GetResult();
        ConnectionString = _container.GetConnectionString();
        Port = _container.GetMappedPublicPort(4566);
    }
}
