using Shouldly;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence;

namespace Wolverine.Http.Tests;

/// <summary>
/// Reproducer for the EF Core outbox flush-before-commit bug surfaced via the sample
/// at https://github.com/dmytro-pryvedeniuk/outbox.
///
/// <see cref="EFCorePersistenceFrameProvider.ApplyTransactionSupport"/> adds a
/// <see cref="FlushOutgoingMessages"/> postprocessor whenever the chain requires the
/// outbox AND <see cref="Wolverine.Configuration.IChain.ShouldFlushOutgoingMessages"/>
/// is true (always true for HttpChain). In Eager mode (the default), the chain ALSO
/// has the <c>EnrollDbContextInTransaction</c> middleware, whose generated code wraps
/// the rest of the chain in a try block ending with
/// <c>efCoreEnvelopeTransaction.CommitAsync(...)</c>.
/// <c>EfCoreEnvelopeTransaction.CommitAsync</c> already flushes outgoing messages —
/// but only AFTER the EF Core DB transaction commits.
///
/// The unconditional postprocessor sits BEFORE that commit, so the generated code is:
/// <code>
/// // Added by EF Core Transaction Middleware
/// var result_of_SaveChangesAsync = await _itemsDbContext.SaveChangesAsync(...);
///
/// // Have to flush outgoing messages just in case Marten did nothing because of #536
/// await messageContext.FlushOutgoingMessagesAsync().ConfigureAwait(false);  // <-- bug: BEFORE commit
///
/// await efCoreEnvelopeTransaction.CommitAsync(...).ConfigureAwait(false);   // <-- this commits + re-flushes
/// </code>
///
/// At runtime the early flush sends the cascading envelope through the transport
/// sender, which then asks
/// <see cref="Wolverine.Persistence.Durability.IMessageOutbox.DeleteOutgoingAsync"/>
/// (running on a separate connection) to remove the wolverine_outgoing row written by
/// <c>SaveChangesAsync</c>. The INSERT is still uncommitted and invisible to the second
/// connection, the DELETE no-ops, the EF Core commit later makes the INSERT visible,
/// and the row is left stranded for the durability agent to re-send (at-least-once
/// instead of exactly-once).
///
/// We assert at the codegen surface rather than the runtime because the symptom
/// (stranded row) is cleaned up by the durability agent within ~250ms (the
/// ScheduledJobPollingTime configured for tests in <c>WolverineWebApi/Program.cs</c>),
/// which races against any post-request DB query in a test. The generated source code
/// is the deterministic proof — if <c>FlushOutgoingMessagesAsync()</c> appears as a
/// standalone postprocessor inside the EnrollDbContextInTransaction try block, the
/// flush ordering is wrong by definition.
/// </summary>
public class Bug_efcore_outbox_flush_before_commit : IntegrationContext
{
    public Bug_efcore_outbox_flush_before_commit(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void http_chain_does_not_flush_outgoing_messages_before_efcore_commit()
    {
        var chain = HttpChains.ChainFor("POST", "/ef/publish");
        chain.ShouldNotBeNull();

        // Direct postprocessor inspection — doesn't depend on dynamic vs. static
        // codegen mode. EnrollDbContextInTransaction's generated code emits
        // CommitAsync on efCoreEnvelopeTransaction at the end of its try block, and
        // CommitAsync itself calls FlushOutgoingMessagesAsync after the DB transaction
        // commits. No standalone FlushOutgoingMessages postprocessor should be present
        // — adding one runs the flush BEFORE the commit and breaks the outbox ordering
        // guarantee.
        chain.Postprocessors.OfType<FlushOutgoingMessages>().ShouldBeEmpty(
            "EFCorePersistenceFrameProvider added a FlushOutgoingMessages postprocessor on this Eager-mode chain. The wrapping EnrollDbContextInTransaction.CommitAsync already flushes after commit; this extra postprocessor runs the flush BEFORE the commit and strands the wolverine_outgoing row.");
    }
}
