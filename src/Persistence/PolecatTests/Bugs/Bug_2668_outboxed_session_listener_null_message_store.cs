using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

/// <summary>
/// Reproducer + regression coverage for GH-2668. Before the fix,
/// <see cref="Wolverine.Polecat.Publishing.OutboxedSessionFactory.buildSessionOptions"/>
/// added a <see cref="Wolverine.Polecat.FlushOutgoingMessagesOnCommit"/> listener with
/// <c>null!</c> for the <c>SqlServerMessageStore</c>, with a comment claiming the store
/// would be set after transaction creation. No such setter ever existed (the listener's
/// field is <c>readonly</c>), so the listener carried <c>null</c> for its lifetime and
/// the first time <c>BeforeSaveChangesAsync</c> read <c>_messageStore.Role</c> it
/// <c>NullReferenceException</c>'d — failing every Polecat-backed Wolverine handler that
/// calls <c>IDocumentSession.SaveChangesAsync</c>.
///
/// The NRE only fires for envelopes that traverse the durable inbox (where
/// <c>Envelope.WasPersistedInInbox = true</c>); that's why the existing PolecatTests
/// suite, which uses <c>InvokeMessageAndWaitAsync</c> against non-durable defaults,
/// doesn't catch it. This test wires <c>UseDurableLocalQueues</c> + sends via
/// <c>SendMessageAndWaitAsync</c> so the local queue persists the envelope to
/// wolverine_incoming_envelopes before the handler runs, which is what triggers the
/// listener's interesting branch.
///
/// Assertion: the document the handler stores actually lands in the document store. With
/// the bug present the handler throws inside <c>SaveChangesAsync</c> and the
/// transaction rolls back; with the fix the document is loadable after handling
/// completes.
/// </summary>
public class Bug_2668_outboxed_session_listener_null_message_store : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType<Bug2668Handler>();

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "bug2668";
                    // Polecat 2.0 defaults UseNativeJsonType=true (SQL Server 2025).
                    // Repo docker-compose pins 2022-latest for Apple Silicon support;
                    // the polecat workflow overrides to 2025-latest in CI. Stay on
                    // string body so the test runs on either image.
                    m.UseNativeJsonType = false;
                }).IntegrateWithWolverine(integration =>
                {
                    // Keep Wolverine's tables in their own schema so a stale ResetState
                    // pass doesn't fight with a different test's wolverine_* tables.
                    integration.MessageStorageSchemaName = "bug2668_wol";
                });

                // Promote the local queue handling Bug2668Command to a durable
                // receiver so the inbox writes the envelope before the handler runs.
                // That's what flips Envelope.WasPersistedInInbox to true and exercises
                // the FlushOutgoingMessagesOnCommit branch the bug lives in.
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)_store).Database.ApplyAllConfiguredChangesToDatabaseAsync();

        await using var session = _store.LightweightSession();
        session.DeleteWhere<Bug2668Doc>(x => true);
        await session.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task handler_against_polecat_session_does_not_NRE_in_BeforeSaveChangesAsync()
    {
        var id = Guid.NewGuid();

        // DoNotAssertOnExceptionsDetected: with the bug present the handler
        // would NRE inside SaveChanges and TrackActivity would otherwise rethrow
        // before we got to the document-state assertion. The assertion below is
        // the user-visible symptom (no document persisted).
        await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new Bug2668Command(id, "Joe Mixon"));

        await using var session = _store.LightweightSession();
        var doc = await session.LoadAsync<Bug2668Doc>(id);
        doc.ShouldNotBeNull(
            "Handler did not persist the document — most likely because BeforeSaveChangesAsync threw NullReferenceException on the null SqlServerMessageStore (GH-2668). Confirm OutboxedSessionFactory.buildSessionOptions passes a real store to FlushOutgoingMessagesOnCommit.");
        doc.Name.ShouldBe("Joe Mixon");
    }
}

public record Bug2668Command(Guid Id, string Name);

public class Bug2668Doc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public class Bug2668Handler
{
    // Takes IDocumentSession so the handler is wired through OutboxedSessionFactory's
    // OpenSession path — the only path that adds the FlushOutgoingMessagesOnCommit
    // listener to SessionOptions.Listeners. Calling session.Store + the implicit
    // SaveChangesAsync inserted by Wolverine's transactional middleware is what
    // triggers BeforeSaveChangesAsync.
    public void Handle(Bug2668Command command, IDocumentSession session)
    {
        session.Store(new Bug2668Doc { Id = command.Id, Name = command.Name });
    }
}
