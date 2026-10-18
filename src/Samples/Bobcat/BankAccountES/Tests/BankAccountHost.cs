using Alba;
using Bobcat.CritterStack;

namespace BankAccountES.Tests;

/// <summary>
/// The application under test, booted once for the run through Alba. Configuration comes from the
/// host's own appsettings.json and the environment, so <c>EventStore=Fisher</c> or
/// <c>ConnectionStrings__Marten=...</c> point the same specs at another store or database.
/// </summary>
// #region app-host
public class BankAccountHost : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await AlbaHost.For<Program>();

        // Not load-bearing for correctness: every spec mints its own ids, so no run can collide
        // with the last one. It is here so the event store does not grow without bound across runs.
        // ResetEventStoresAsync empties every store the host registers -- documents AND streams --
        // through JasperFx.Events.IEventStore, so it reads the same on Marten and Fisher.
        await Host.ResetEventStoresAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }
}
// #endregion app-host
