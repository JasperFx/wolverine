using JasperFx;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime;

// Regression for #3130: ResourceMigrationFailureMode must govern Wolverine's OWN startup
// infrastructure work (transport init / auto-provision and store migration), not just the
// JasperFx resource-setup hosted service. A transport that fails to initialize should abort
// startup under FailFast (the default) but be logged-and-skipped under ContinueOnFailures.
public class resource_migration_failure_mode_on_startup
{
    [Fact]
    public async Task fail_fast_is_the_default_and_aborts_startup()
    {
        await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.Transports.Add(new ThrowingTransport()))
                .StartAsync();
        });
    }

    [Fact]
    public async Task continue_on_failures_lets_the_application_start()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ResourceMigrationFailureMode = ResourceMigrationFailureMode.ContinueOnFailures;
                opts.Transports.Add(new ThrowingTransport());
            })
            .StartAsync();

        // If we got here, startup continued despite the transport's InitializeAsync throwing
        host.Services.GetService(typeof(IWolverineRuntime)).ShouldNotBeNull();
    }
}

internal class ThrowingTransport : TransportBase<Endpoint>
{
    public ThrowingTransport() : base("throwing", "Throwing Test Transport", ["throwing"])
    {
    }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
        => throw new InvalidOperationException("Simulated transport initialization failure for #3130");

    protected override IEnumerable<Endpoint> endpoints() => [];

    protected override Endpoint findEndpointByUri(Uri uri)
        => throw new NotSupportedException();
}
