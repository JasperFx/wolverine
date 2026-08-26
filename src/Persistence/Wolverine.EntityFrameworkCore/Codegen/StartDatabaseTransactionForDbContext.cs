using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Codegen;

// Multi-tenant counterpart to EnrollDbContextInTransaction. Begins the transaction and provides the
// try/catch rollback wrapper; the commit + outbox flush is emitted by the
// CommitTenantedDbContextTransaction postprocessor (added by EFCorePersistenceFrameProvider) so it
// runs before the HTTP response writer. See GH-2917.
internal class StartDatabaseTransactionForDbContext : AsyncFrame
{
    private readonly Type _dbContextType;
    private readonly IdempotencyStyle _idempotencyStyle;

    private Variable _dbContext = null!;
    private Variable _cancellation = null!;
    private Variable? _context;

    public StartDatabaseTransactionForDbContext(Type dbContextType, IdempotencyStyle idempotencyStyle)
    {
        _dbContextType = dbContextType;
        _idempotencyStyle = idempotencyStyle;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write("BLOCK:try");

        writer.Write($"BLOCK:if ({_dbContext.Usage}.Database.CurrentTransaction == null)");
        writer.Write($"await {_dbContext.Usage}.Database.BeginTransactionAsync({_cancellation.Usage}).ConfigureAwait(false);");
        writer.FinishBlock();

        // EF Core can only do eager idempotent checks. GH-4128: this is emitted exactly once, and
        // after the transaction is opened, matching the single-DbContext sibling
        // EnrollDbContextInTransaction. A second copy above the BeginTransactionAsync block used to
        // run the same inbox existence query twice per message on the paths where
        // AssertEagerIdempotencyAsync does not set Envelope.WasPersistedInInbox.
        if (_idempotencyStyle == IdempotencyStyle.Eager || _idempotencyStyle == IdempotencyStyle.Optimistic)
        {
            writer.Write($"await {_context!.Usage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation.Usage}).ConfigureAwait(false);");
        }

        // The commit + outbox flush is NOT emitted here anymore - see CommitTenantedDbContextTransaction
        // (added as a postprocessor by EFCorePersistenceFrameProvider). It runs BEFORE the HTTP response
        // writer while still inside this try/catch, so the commit + MessageContext flush complete before
        // the response is written. See GH-2917.
        Next?.GenerateCode(method, writer);

        writer.FinishBlock();
        writer.Write($"BLOCK:catch ({typeof(Exception).FullNameInCode()})");
        writer.Write($"await {_dbContext.Usage}.Database.RollbackTransactionAsync({_cancellation.Usage}).ConfigureAwait(false);");
        writer.Write("throw;");
        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _dbContext = chain.FindVariable(_dbContextType);
        yield return _dbContext;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}

/// <summary>
/// Commits the multi-tenant EF Core database transaction started by
/// <see cref="StartDatabaseTransactionForDbContext" /> and then flushes the MessageContext's outgoing
/// messages. Emitted as a postprocessor so it runs before the HTTP response writer - committing
/// before flushing (so the post-send outbox bookkeeping sees committed rows) and flushing before the
/// response is written (so TrackActivity observes the sent envelopes). Implements
/// <see cref="IFlushesMessages" /> so the chain does not also add a standalone FlushOutgoingMessages
/// postprocessor (which would flush after the response, and before the commit). See GH-2917.
/// </summary>
/// <remarks>
/// Before committing, this frame runs any registered <see cref="IDomainEventScraper" />s against the
/// tenant DbContext exactly as <see cref="Wolverine.EntityFrameworkCore.Internals.EfCoreEnvelopeTransaction.CommitAsync" />
/// does on the single-DbContext path (via <see cref="CommitEfCoreEnvelopeTransaction" />). Unlike that
/// path, the multi-tenant DbContext is created at runtime inside
/// <see cref="Wolverine.EntityFrameworkCore.Internals.IDbContextBuilder{T}.BuildAndEnrollAsync" />, so the
/// enlisted <see cref="Wolverine.EntityFrameworkCore.Internals.EfCoreEnvelopeTransaction" /> is never
/// surfaced as a codegen variable and its <c>CommitAsync</c> is not in the generated chain. The scrape
/// loop is therefore inlined here so that <c>PublishDomainEventsFromEntityFrameworkCore</c> works under
/// managed multi-tenancy too.
/// </remarks>
internal class CommitTenantedDbContextTransaction : AsyncFrame, IFlushesMessages
{
    private readonly Type _dbContextType;
    private Variable _dbContext = null!;
    private Variable _context = null!;
    private Variable _cancellation = null!;
    private Variable _scrapers = null!;

    public CommitTenantedDbContextTransaction(Type dbContextType)
    {
        _dbContextType = dbContextType;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(
            "Scrape any domain events out of the tenant DbContext before committing (mirrors EfCoreEnvelopeTransaction.CommitAsync)");
        writer.Write($"BLOCK:foreach (var scraper in {_scrapers.Usage})");
        writer.Write($"await scraper.{nameof(IDomainEventScraper.ScrapeEvents)}({_dbContext.Usage}, {_context.Usage}).ConfigureAwait(false);");
        writer.FinishBlock();

        // GH-3744: this scrape runs as a postprocessor AFTER the SaveChangesAsync that
        // EFCorePersistenceFrameProvider emits, and a durable route persists its envelope by adding an
        // OutgoingMessage/IncomingMessage entity to the change tracker
        // (EfCoreEnvelopeTransaction.PersistOutgoingAsync on a Wolverine-enabled DbContext). Committing
        // straight after the scrape therefore committed the aggregate but dropped every envelope the
        // scrape had just produced -- the domain event was still *published* in memory, so tracked-session
        // assertions passed while the durable row was silently missing. Flush the tracker again so the
        // envelopes land inside this transaction. A no-op when the scrape produced nothing.
        writer.WriteComment("GH-3744: persist any envelopes the scrape just tracked, inside this transaction");
        writer.Write($"await {_dbContext.Usage}.SaveChangesAsync({_cancellation.Usage}).ConfigureAwait(false);");

        writer.WriteComment(
            "Commit the EF Core transaction and flush outgoing messages before writing the response (GH-2917)");
        writer.Write($"await {_dbContext.Usage}.Database.CommitTransactionAsync({_cancellation.Usage}).ConfigureAwait(false);");
        writer.Write($"await {_context.Usage}.{nameof(MessageContext.FlushOutgoingMessagesAsync)}().ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _scrapers = chain.FindVariable(typeof(IEnumerable<IDomainEventScraper>));
        yield return _scrapers;

        _dbContext = chain.FindVariable(_dbContextType);
        yield return _dbContext;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}
