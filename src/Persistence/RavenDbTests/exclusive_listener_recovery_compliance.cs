using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Wolverine.RavenDb;

namespace RavenDbTests;

/// <summary>
/// GH-3590/GH-3595 -- see <see cref="ExclusiveListenerRecoveryCompliance"/>. The last scenario is what pins
/// the <c>RavenDbDurabilityAgent</c> skipping any destination whose <c>ListenerScope</c> is not
/// <c>CompetingConsumers</c>.
/// </summary>
[Collection("raven")]
public class exclusive_listener_recovery_compliance : ExclusiveListenerRecoveryCompliance
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;

    public exclusive_listener_recovery_compliance(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public override ValueTask InitializeAsync()
    {
        // RavenTestDriver hands back a brand new database on every call and xUnit builds one instance of
        // this class per test method, so each scenario gets its own isolated inbox.
        _store = _fixture.StartRavenStore();
        return ValueTask.CompletedTask;
    }

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.UseRavenDbPersistence();

        // Registered as an instance rather than a factory so the host's container does not dispose the
        // store out from under the DatabaseFixture that owns it
        options.Services.AddSingleton<IDocumentStore>(_store);
    }
}
