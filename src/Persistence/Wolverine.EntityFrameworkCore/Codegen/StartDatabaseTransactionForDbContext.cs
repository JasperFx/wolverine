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

        // GH-4239: deliberately NOT `RollbackTransactionAsync(cancellation)`. That call threw over the
        // exception being unwound whenever there was no transaction to roll back (an already-cancelled
        // token means BeginTransactionAsync never created one), and it silently skipped the rollback
        // when the token was cancelled after the transaction opened. Either way the real failure never
        // reached a log or a dead letter queue. See EfCoreTransactionRollback.
        writer.Write(
            $"await {typeof(EfCoreTransactionRollback).FullNameInCode()}.{nameof(EfCoreTransactionRollback.SafeRollbackAsync)}({_dbContext.Usage}).ConfigureAwait(false);");
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
/// <see cref="StartDatabaseTransactionForDbContext" />. Emitted as a postprocessor so it runs before the
/// HTTP response writer - committing before the flush (so the post-send outbox bookkeeping sees committed
/// rows) and both before the response is written (so TrackActivity observes the sent envelopes). See
/// GH-2917.
/// <para>GH-4742: the flush used to be the last statement of THIS frame. It is now emitted immediately
/// after it by <see cref="FlushOutboxAfterCommit" />, which carries the <see cref="IFlushesMessages" />
/// marker this frame used to carry, so the chain still does not add a standalone (pre-commit)
/// FlushOutgoingMessages — and a postprocessor can now sit strictly between the commit and the flush.
/// The emitted sequence is unchanged.</para>
/// </summary>
/// <remarks>
/// Before committing, this frame runs any registered <see cref="IDomainEventScraper" />s against the
/// tenant DbContext exactly as <see cref="EfCoreEnvelopeTransaction.CommitAsync(System.Threading.CancellationToken, bool)" />
/// does on the single-DbContext path (via <see cref="CommitEfCoreEnvelopeTransaction" />). Unlike that
/// path, the multi-tenant DbContext is created at runtime inside
/// <see cref="Wolverine.EntityFrameworkCore.Internals.IDbContextBuilder{T}.BuildAndEnrollAsync" />, so the
/// enlisted <see cref="Wolverine.EntityFrameworkCore.Internals.EfCoreEnvelopeTransaction" /> is never
/// surfaced as a codegen variable. <see cref="EfCoreTenantedTransaction.CommitAsync" /> finds it on the
/// <see cref="MessageContext" /> at runtime and commits through it, which runs the scrapers and -- E3 -- marks a
/// durable inbox row Handled inside the tenant transaction. It falls back to scraping and committing the DbContext
/// directly, as this frame used to inline, when the context is enlisted with something else.
/// </remarks>
internal class CommitTenantedDbContextTransaction : AsyncFrame
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
        // E3: commit through the EfCoreEnvelopeTransaction the tenant DbContext was enlisted with, so the durable
        // inbox row is marked Handled inside this transaction exactly as on the single-database path. The helper
        // still scrapes domain events first and saves what the scrape tracked (GH-3744) before committing
        writer.WriteComment(
            "Scrape domain events, mark the inbox row handled and commit the EF Core transaction before writing the response (GH-2917). The outbox flush follows in its own frame (GH-4742)");
        writer.Write(
            $"await {typeof(EfCoreTenantedTransaction).FullNameInCode()}.{nameof(EfCoreTenantedTransaction.CommitAsync)}({_dbContext.Usage}, {_context.Usage}, {_scrapers.Usage}, {_cancellation.Usage}).ConfigureAwait(false);");
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
