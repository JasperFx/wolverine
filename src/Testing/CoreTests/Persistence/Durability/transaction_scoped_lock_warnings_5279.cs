using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using CoreTests.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Durability;

// GH-5279. DurabilitySettings.UseTransactionScopedDurabilityPassLock is a core setting that only PostgreSQL honors,
// and a pass's extension steps run while that lock holds an open transaction. Both get a warning, once per store
public class transaction_scoped_lock_warnings_5279 : IAsyncLifetime
{
    private readonly CapturedLogs theLogs = new();
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(theLogs))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.UseTransactionScopedDurabilityPassLock = true;
                opts.Discovery.DisableConventionalDiscovery();
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task runTwiceAsync(IReadOnlyList<IMessageStore> stores, params IDurabilityPassStep[] extensionSteps)
    {
        var runner = new DurabilityPassRunner(_host.GetRuntime());
        for (var i = 0; i < 2; i++)
        {
            await runner.RunAsync(stores, extensionSteps, new DurabilityPassOptions(),
                TestContext.Current.CancellationToken);
        }
    }

    private string[] lockWarnings() =>
        theLogs.Warnings.Where(x => x.Contains("transaction-scoped durability pass lock")).ToArray();

    [Fact]
    public async Task warns_once_that_a_store_without_support_ignores_the_setting()
    {
        var store = new FakePassStore("sqlserver", MessageStoreRole.Main, new RecordingStep("outgoing", DurabilityPassStepResult.Nothing));

        await runTwiceAsync([store]);

        var warning = lockWarnings().ShouldHaveSingleItem();
        warning.ShouldContain("does not support");
        warning.ShouldContain(store.Uri.ToString());
    }

    [Fact]
    public async Task warns_once_that_extension_steps_run_inside_the_open_transaction()
    {
        var store = new FakePassStore("postgresql", MessageStoreRole.Main, new RecordingStep("outgoing", DurabilityPassStepResult.Nothing))
        {
            SupportsTransactionScopedDurabilityPassLock = true
        };

        await runTwiceAsync([store], new RecordingStep("projections", DurabilityPassStepResult.Nothing));

        var warning = lockWarnings().ShouldHaveSingleItem();
        warning.ShouldContain("projections");
        warning.ShouldContain("event sequence gap");
    }

    [Fact]
    public async Task no_warning_for_a_supporting_store_without_extension_steps()
    {
        var store = new FakePassStore("postgresql", MessageStoreRole.Main, new RecordingStep("outgoing", DurabilityPassStepResult.Nothing))
        {
            SupportsTransactionScopedDurabilityPassLock = true
        };

        await runTwiceAsync([store]);

        lockWarnings().ShouldBeEmpty();
    }
}
