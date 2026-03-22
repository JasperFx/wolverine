using Testcontainers.Nats;
using Xunit;

namespace Wolverine.Nats.Tests;

public class NatsContainerFixture : IAsyncLifetime
{
    private static NatsContainer? _container;
    private static string? _connectionString;
    private static int _referenceCount;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public string ConnectionString => _connectionString!;

    public async Task InitializeAsync()
    {
        await Lock.WaitAsync();
        try
        {
            _referenceCount++;
            if (_container != null) return;

            _container = new NatsBuilder()
                .WithImage("nats:latest")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
            Environment.SetEnvironmentVariable("NATS_URL", _connectionString);
        }
        finally
        {
            Lock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await Lock.WaitAsync();
        try
        {
            _referenceCount--;
            if (_referenceCount > 0 || _container == null) return;

            Environment.SetEnvironmentVariable("NATS_URL", null);
            await _container.DisposeAsync();
            _container = null;
            _connectionString = null;
        }
        finally
        {
            Lock.Release();
        }
    }
}
