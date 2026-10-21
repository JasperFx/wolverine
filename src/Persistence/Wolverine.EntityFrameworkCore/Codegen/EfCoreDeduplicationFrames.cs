using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence.Codegen;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// GH-5108. Claims the logical deduplication id on the DbContext's own connection and transaction, producing
/// the same <c>isDuplicateMessage</c> the claim-and-release path produces, so every chain type's refusal works
/// unchanged.
///
/// <para>
/// Unlike Marten's optimistic check, this IS the claim. EF Core already has the transaction open by now, so
/// the INSERT can run immediately: a concurrent copy of the same id blocks on this transaction's uncommitted
/// row and is refused once it commits, which leaves no commit-time race to convert. The dependency on
/// <see cref="EfCoreEnvelopeTransaction" /> is what places this after <see cref="EnrollDbContextInTransaction" />,
/// inside its try/catch, so a rollback or a crash takes the claim with it.
/// </para>
/// </summary>
internal class ClaimDeduplicationIdInEfCoreTransactionFrame : AsyncFrame, IDeduplicationClaimFrame
{
    private readonly Variable _deduplicationId;
    private readonly TimeSpan? _window;
    private Variable? _envelopeTransaction;
    private Variable? _cancellation;

    public ClaimDeduplicationIdInEfCoreTransactionFrame(Variable deduplicationId, TimeSpan? window)
    {
        _deduplicationId = deduplicationId;
        _window = window;

        // Deliberately the same variable name the non-transactional frame uses
        Variable = new Variable(typeof(bool), "isDuplicateMessage", this);
    }

    /// <summary>
    /// <see langword="true" /> when this execution must be refused as a duplicate.
    /// </summary>
    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var window = _window.HasValue
            ? $"{typeof(TimeSpan).FullNameInCode()}.{nameof(TimeSpan.FromTicks)}({_window.Value.Ticks})"
            : "null";

        writer.WriteComment("GH-5108: claim the logical deduplication id inside this EF Core transaction");
        writer.Write($"var {Variable.Usage} = false;");
        writer.Write($"BLOCK:if (!string.IsNullOrWhiteSpace({_deduplicationId.Usage}))");
        writer.Write(
            $"{Variable.Usage} = !(await {_envelopeTransaction!.Usage}.{nameof(EfCoreEnvelopeTransaction.TryClaimDeduplicationIdAsync)}({_deduplicationId.Usage}, {window}, {_cancellation!.Usage}).ConfigureAwait(false));");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        _envelopeTransaction = chain.FindVariable(typeof(EfCoreEnvelopeTransaction));
        yield return _envelopeTransaction;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}

/// <summary>
/// GH-5108. The <see cref="Wolverine.Persistence.TransactionalDeduplication.Claim" /> slot. There is nothing
/// left to enlist -- <see cref="ClaimDeduplicationIdInEfCoreTransactionFrame" /> already wrote the claim on
/// the transaction -- so this only says so, after the refusal, in the generated source.
/// </summary>
internal class EfCoreDeduplicationClaimHeldFrame : SyncFrame
{
    public EfCoreDeduplicationClaimHeldFrame(Variable isDuplicate)
    {
        // Not read; declared so the arranger emits this after the refusal rather than ahead of the transaction
        uses.Add(isDuplicate);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(
            "GH-5108: the claim is held by the EF Core transaction, so it commits or rolls back with this work and needs no release");
        Next?.GenerateCode(method, writer);
    }
}
