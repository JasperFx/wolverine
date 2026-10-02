using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// GH-3291: enrolls a Wolverine-enabled DbContext + the IMessageContext in the outgoing outbox
/// transaction WITHOUT beginning an explicit database transaction. Used for Wolverine.Http endpoints in
/// <see cref="Wolverine.Persistence.TransactionMiddlewareMode.Lightweight"/> mode.
///
/// Unlike a message handler — whose MessageContext is enlisted at runtime by
/// <c>MessageContext.ReadEnvelope</c> when it reads the incoming envelope — an HTTP endpoint has no
/// incoming envelope, so its <c>MessageContext.Transaction</c> is otherwise null. In Lightweight mode
/// that means <c>MessageBus.PersistOrSendAsync</c> takes the send-now branch and cascaded messages are
/// dispatched BEFORE the <c>SaveChangesAsync</c> postprocessor commits. Enlisting here makes those
/// cascades buffer and flush after the commit instead.
///
/// This deliberately does NOT call <c>BeginTransactionAsync</c> (that is what
/// <see cref="EnrollDbContextInTransaction"/> does for Eager mode): the write is covered by the implicit
/// transaction <c>SaveChangesAsync</c> opens, and skipping the explicit begin keeps this compatible with
/// EF Core's retrying execution strategy (<c>EnableRetryOnFailure</c>), which forbids user-initiated
/// transactions. Implements <see cref="IFlushesMessages"/> so <c>HttpChain</c> does not also add a
/// standalone (pre-commit) <c>FlushOutgoingMessages</c>; the paired
/// <see cref="CommitEfCoreEnvelopeTransaction"/> postprocessor performs the post-commit flush.
///
/// <para>GH-4630: message handlers use this frame too. Their MessageContext <em>is</em> enlisted at
/// runtime -- as itself -- so their cascades were already buffered rather than sent early; but
/// <c>MessageContext</c>-as-transaction writes those envelopes with
/// <c>Envelope.StoreAndForwardAsync()</c>, a second database write that the handler's own
/// <c>SaveChangesAsync</c> knows nothing about. Enlisting the DbContext instead puts the envelope rows
/// in the same unit of work as the entity changes. The post-SaveChanges completion for that path is
/// <see cref="ScrapeDomainEventsAndSaveChanges" /> rather than
/// <see cref="CommitEfCoreEnvelopeTransaction" />, because a message handler's inbox bookkeeping is
/// still owned by the pipeline.</para>
/// </summary>
internal class EnlistDbContextInOutbox : AsyncFrame, IFlushesMessages
{
    private readonly Type _dbContextType;
    private readonly IdempotencyStyle _idempotencyStyle;
    private readonly Variable _envelopeTransaction;
    private Variable _dbContext = null!;
    private Variable? _context;
    private Variable _scrapers = null!;
    private Variable _cancellation = null!;

    public EnlistDbContextInOutbox(Type dbContextType, IdempotencyStyle idempotencyStyle = IdempotencyStyle.None)
    {
        _dbContextType = dbContextType;
        _idempotencyStyle = idempotencyStyle;
        _envelopeTransaction = new Variable(typeof(EfCoreEnvelopeTransaction), this);
    }

    private bool emitsIdempotencyCheck =>
        _idempotencyStyle is IdempotencyStyle.Eager or IdempotencyStyle.Optimistic;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment(
            "GH-3291: enroll the DbContext & IMessagingContext in the outbox so cascaded messages buffer");
        writer.WriteComment(
            "and flush AFTER SaveChangesAsync commits. No explicit transaction is started (Lightweight mode).");
        writer.Write($"var {_envelopeTransaction.Usage} = new {typeof(EfCoreEnvelopeTransaction).FullNameInCode()}({_dbContext.Usage}, {_context!.Usage}, {_scrapers.Usage});");
        writer.Write($"await {_context.Usage}.{nameof(MessageContext.EnlistInOutboxAsync)}({_envelopeTransaction.Usage}).ConfigureAwait(false);");

        // GH-4630: Lightweight chains report IsTransactional = true, so
        // EagerIdempotencyOnNonTransactionalChains skips them -- and the frames that emit this call
        // only ever ran on the Eager paths. AutoApplyTransactions(IdempotencyStyle.Eager) plus
        // Lightweight therefore emitted no inbox check at all. The check's INSERT works perfectly well
        // inside SaveChanges' implicit transaction.
        if (emitsIdempotencyCheck)
        {
            writer.Write($"await {_context.Usage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation.Usage}).ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Mirrors the C# body inside an async `task { }` computation expression: awaits become `do!`
        // and `.ConfigureAwait(false)` is dropped (the CE controls scheduling). See
        // EnrollDbContextInTransaction for the same conventions.
        writer.Write("");
        writer.WriteComment(
            "GH-3291: enroll the DbContext & IMessagingContext in the outbox (Lightweight mode, no explicit transaction)");
        writer.Write($"{_envelopeTransaction.FSharpAssignmentUsage} = {typeof(EfCoreEnvelopeTransaction).FSharpName()}({_dbContext.FSharpUsage}, {_context!.FSharpUsage}, {_scrapers.FSharpUsage})");
        writer.Write($"do! {_context.FSharpUsage}.{nameof(MessageContext.EnlistInOutboxAsync)}({_envelopeTransaction.FSharpUsage})");

        if (emitsIdempotencyCheck)
        {
            writer.Write($"do! {_context.FSharpUsage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation.FSharpUsage})");
        }

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _scrapers = chain.FindVariable(typeof(IEnumerable<IDomainEventScraper>));
        yield return _scrapers;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _dbContext = chain.FindVariable(_dbContextType);
        yield return _dbContext;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}

/// <summary>
/// The post-<c>SaveChangesAsync</c> completion for a <see cref="TransactionMiddlewareMode.Lightweight" />
/// chain whose DbContext is enlisted in the outbox. Three things in order:
/// <list type="number">
/// <item>run every registered <see cref="IDomainEventScraper" />. GH-4630: the scrapers only ever ran
/// inside <c>EfCoreEnvelopeTransaction.CommitAsync</c>, <c>CommitTenantedDbContextTransaction</c> and
/// <c>DbContextOutbox.SaveChangesAndFlushMessagesAsync</c>, none of which is on a Lightweight message
/// handler's path -- so <c>PublishDomainEventsFromEntityFrameworkCore()</c> was inert there;</item>
/// <item><c>SaveChangesAsync</c> again, because a durable route persists its envelope by adding an
/// entity to the change tracker and the scrape runs AFTER the middleware's own save. This is the same
/// GH-3744 trap <see cref="CommitTenantedDbContextTransaction" /> documents;</item>
/// <item>commit any transaction that got opened along the way. Lightweight mode never begins one, but a
/// DbContext WITHOUT the Wolverine envelope mappings makes
/// <c>EfCoreEnvelopeTransaction.PersistOutgoingAsync</c> open one itself for its raw ADO write -- and
/// nothing else in a Lightweight chain would ever commit it.</item>
/// </list>
/// <para>GH-4742: this frame never flushes. A chain that needs the buffered cascades sent gets
/// <see cref="FlushOutboxAfterCommit" /> emitted immediately after it, so that a postprocessor can sit
/// strictly between the two.</para>
/// </summary>
internal class ScrapeDomainEventsAndSaveChanges : AsyncFrame
{
    private readonly Type _dbContextType;
    private Variable _dbContext = null!;
    private Variable _context = null!;
    private Variable _cancellation = null!;
    private Variable _scrapers = null!;

    public ScrapeDomainEventsAndSaveChanges(Type dbContextType)
    {
        _dbContextType = dbContextType;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(
            "GH-4630: scrape any domain events out of the DbContext (mirrors EfCoreEnvelopeTransaction.CommitAsync)");
        writer.Write($"BLOCK:foreach (var scraper in {_scrapers.Usage})");
        writer.Write($"await scraper.{nameof(IDomainEventScraper.ScrapeEvents)}({_dbContext.Usage}, {_context.Usage}).ConfigureAwait(false);");
        writer.FinishBlock();

        writer.WriteComment("GH-3744: persist any envelopes the scrape just tracked");
        writer.Write($"await {_dbContext.Usage}.SaveChangesAsync({_cancellation.Usage}).ConfigureAwait(false);");

        writer.WriteComment(
            "An unmapped DbContext writes envelopes with raw ADO inside a transaction it opens itself");
        writer.Write($"BLOCK:if ({_dbContext.Usage}.Database.CurrentTransaction != null)");
        writer.Write($"await {_dbContext.Usage}.Database.CommitTransactionAsync({_cancellation.Usage}).ConfigureAwait(false);");
        writer.FinishBlock();

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
