using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace MartenTests;

/// <summary>
/// Codegen-only checks for the Marten side of <c>Tracking.OutboxDiagnosticsEnabled</c>:
/// when the flag is on, the Marten transactional middleware's
/// <c>SaveChangesAsync</c> postprocessor must be bracketed with
/// <c>marten.savechanges.start</c> / <c>marten.savechanges.finished</c> ActivityEvents
/// in the generated handler source. When the flag is off, those calls must not appear
/// at all (no runtime <c>if/then</c> guard — the gate is purely codegen-time).
/// Both tests dump the generated source for the target handler chain to xUnit output
/// so reviewers can read the contract.
/// </summary>
public class marten_tracking_diagnostics : PostgresqlContext
{
    private readonly ITestOutputHelper _output;

    public marten_tracking_diagnostics(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task save_changes_events_baked_into_codegen_when_outbox_diagnostics_enabled()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();
                // AutoApplyTransactions wires the Marten persistence frame provider's
                // ApplyTransactionSupport onto chains that touch Marten — that's what
                // adds the DocumentSessionSaveChanges postprocessor we want to wrap
                // with marten.savechanges.start / .finished ActivityEvents.
                opts.Policies.AutoApplyTransactions();
                opts.Tracking.OutboxDiagnosticsEnabled = true;
            }).StartAsync();

        // Force codegen by resolving the handler.
        host.GetRuntime().Handlers.HandlerFor<MartenTrackingMessage>();

        var chain = host.GetRuntime().Handlers.ChainFor<MartenTrackingMessage>();
        chain.ShouldNotBeNull();
        chain.SourceCode.ShouldNotBeNull();

        _output.WriteLine("=== Generated source for MartenTrackingHandler (OutboxDiagnosticsEnabled = true) ===");
        _output.WriteLine(chain.SourceCode);

        chain.SourceCode.ShouldContain($"\"{MartenTracing.MartenSaveChangesStarted}\"");
        chain.SourceCode.ShouldContain($"\"{MartenTracing.MartenSaveChangesFinished}\"");
    }

    [Fact]
    public async Task save_changes_events_absent_from_codegen_when_outbox_diagnostics_disabled()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();
                opts.Policies.AutoApplyTransactions();
                // OutboxDiagnosticsEnabled left at its default (false)
            }).StartAsync();

        host.GetRuntime().Handlers.HandlerFor<MartenTrackingMessage>();

        var chain = host.GetRuntime().Handlers.ChainFor<MartenTrackingMessage>();
        chain.ShouldNotBeNull();
        chain.SourceCode.ShouldNotBeNull();

        _output.WriteLine("=== Generated source for MartenTrackingHandler (OutboxDiagnosticsEnabled = false, default) ===");
        _output.WriteLine(chain.SourceCode);

        chain.SourceCode.ShouldNotContain($"\"{MartenTracing.MartenSaveChangesStarted}\"");
        chain.SourceCode.ShouldNotContain($"\"{MartenTracing.MartenSaveChangesFinished}\"");
    }
}

public record MartenTrackingMessage(Guid Id);

public static class MartenTrackingHandler
{
    // Pulling in IDocumentSession forces the Marten persistence frame provider
    // to apply transactional middleware to this chain — that's what gives us a
    // DocumentSessionSaveChanges postprocessor for the tracking flag to wrap.
    public static void Handle(MartenTrackingMessage message, IDocumentSession session)
    {
        // no-op — we only care about the generated chain shape
    }
}
