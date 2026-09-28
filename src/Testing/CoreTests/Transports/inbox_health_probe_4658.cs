using CoreTests.Runtime;
using JasperFx;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// GH-4658. <see cref="InboxHealthRestarter" /> is the ONLY thing that resumes a listener paused by
/// PauseForInboxRecoveryAsync -- the pause is indefinite and purely probe-driven. Its probe used to go
/// through <c>runtime.Storage.Inbox</c>, which on a <see cref="MultiTenantedMessageStore" /> fans out
/// over the main store AND every active tenant database and throws if ANY of them refuses.
///
/// <para>
/// Since GH-4435 a listener pauses only for a failure that reached the MAIN store; a tenant database
/// refusing a write defers those envelopes and leaves the listener running. So probing every tenant store
/// to decide whether to resume contradicted the rule the pause itself follows, and one unrelated tenant's
/// outage held a listener paused for its whole duration.
/// </para>
/// </summary>
public class inbox_health_probe_4658
{
    private readonly IMessageStore theMainStore = Substitute.For<IMessageStore>();
    private readonly IMessageStore theDownTenantStore = Substitute.For<IMessageStore>();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();
    private readonly IListenerCircuit theCircuit = Substitute.For<IListenerCircuit>();
    private readonly TaskCompletionSource theRestart = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IWolverineRuntime theRuntime = Substitute.For<IWolverineRuntime>();

    public inbox_health_probe_4658()
    {
        var storeRuntime = new MockWolverineRuntime();
        storeRuntime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;

        theSource.AllActive().Returns([theDownTenantStore]);

        theRuntime.Storage.Returns(new MultiTenantedMessageStore(theMainStore, storeRuntime, theSource));

        // Fully qualified: CoreTests.Transports declares a StubEndpoint of its own, and a type in the
        // enclosing namespace wins over any using directive.
        theCircuit.Endpoint.Returns(new Wolverine.Transports.Stub.StubEndpoint("one", new StubTransport()));
        theCircuit.StartAsync().Returns(_ =>
        {
            theRestart.TrySetResult();
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// The probe's back-off is 2s, then 3s, then 4.5s, so any window shorter than the first 2s proves nothing
    /// either way. Six seconds spans two whole attempts, which is enough to say a listener did NOT resume.
    /// </summary>
    private async Task<bool> listenerWasRestartedAsync(int withinSeconds)
    {
        using var restarter = new InboxHealthRestarter(theCircuit, theRuntime, NullLogger.Instance);

        try
        {
            await theRestart.Task.WaitAsync(TimeSpan.FromSeconds(withinSeconds));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    [Fact]
    public async Task one_downed_tenant_database_does_not_hold_the_listener_paused()
    {
        theDownTenantStore.Inbox.ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>())
            .Throws(new TimeoutException("tenant database is unreachable"));

        // The main store -- the only store whose failure can pause a listener at all -- is fine.
        (await listenerWasRestartedAsync(10))
            .ShouldBeTrue("the listener should resume as soon as the MAIN store answers");
    }

    /// <summary>
    /// The negative control. Narrowing the probe must not turn it into a probe that always passes: a
    /// listener paused because the main store went down still has to stay paused until it comes back,
    /// however healthy every tenant database is.
    /// </summary>
    [Fact]
    public async Task a_downed_main_store_still_holds_the_listener_paused()
    {
        theMainStore.Inbox.ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>())
            .Throws(new TimeoutException("the main database is unreachable"));

        (await listenerWasRestartedAsync(6))
            .ShouldBeFalse("the listener must stay paused while the main store refuses the probe");
    }
}
