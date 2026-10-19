using Alba;

namespace CleanArchitectureTodos.Tests;

/// <summary>
/// The application under test, booted once for the run through Alba. The connection string comes
/// from the host's own appsettings.json, or from <c>ConnectionStrings__Marten</c> in the environment,
/// which is how <c>./build.sh Samples</c> points it at its own database.
/// </summary>
public class CleanArchitectureTodosHost : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Host = await AlbaHost.For<Program>();

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }
}
