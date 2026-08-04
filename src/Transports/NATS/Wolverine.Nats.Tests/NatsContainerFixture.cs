using Testcontainers.Nats;
using Xunit;

namespace Wolverine.Nats.Tests;

public class NatsContainerFixture : IAsyncLifetime
{
    /// <summary>
    /// GH-3799. Pinned, and shared by every NATS container in this project so the version lives in
    /// one place instead of three string literals that can drift apart.
    ///
    /// <para>2.14.4 is exactly what <c>nats:latest</c> resolved to when this was pinned, so nothing
    /// about what CI runs changes — it just stops changing on its own. A rolling tag is pulled fresh
    /// on every CI run and never on a developer machine, which is how a moving
    /// <c>servicebus-emulator</c> spent four runs looking like a code regression (GH-3783).</para>
    /// </summary>
    public const string NatsImage = "nats:2.14.4";

    private static NatsContainer? _container;
    private static string? _connectionString;
    private static int _referenceCount;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public string ConnectionString => _connectionString!;

    public async ValueTask InitializeAsync()
    {
        await Lock.WaitAsync();
        try
        {
            _referenceCount++;
            if (_container != null) return;

            _container = new NatsBuilder()
                .WithImage(NatsImage)
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

    public async ValueTask DisposeAsync()
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
