using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
/// GH-4239: rolls back a failed EF Core transaction without ever displacing the exception that
/// caused the rollback.
///
/// <para>The generated <c>catch</c> in <see cref="Codegen.StartDatabaseTransactionForDbContext" />
/// used to call <c>RollbackTransactionAsync(cancellation)</c> unguarded, which lost the original
/// exception in two separate ways.</para>
///
/// <para>First, when the cancellation token was <b>already</b> cancelled on the way in -- Wolverine's
/// own <c>DefaultExecutionTimeout</c> does exactly this to a nested <c>InvokeAsync</c> --
/// <c>BeginTransactionAsync</c> threw without creating a transaction, and the rollback in the catch
/// then threw <c>InvalidOperationException: The connection does not have any active transactions</c>.
/// That second exception escaped the catch block, so the <c>throw;</c> was never reached and the real
/// failure never appeared in a log or a dead letter queue.</para>
///
/// <para>Second, when the token was cancelled <b>after</b> the transaction was opened, the rollback
/// was handed that same cancelled token and threw <c>TaskCanceledException</c> instead of rolling
/// back -- leaving the transaction open until the DbContext was disposed.</para>
///
/// <para>Hence all three rules below: only roll back a transaction that exists, never let the token
/// that caused the failure also cancel the cleanup, and never let a failed rollback replace the
/// original exception.</para>
/// </summary>
public static class EfCoreTransactionRollback
{
    /// <summary>
    /// Roll back <paramref name="dbContext" />'s current transaction if it has one, swallowing any
    /// failure of the rollback itself. Always safe to call from a catch block.
    /// </summary>
    public static async Task SafeRollbackAsync(DbContext dbContext)
    {
        if (dbContext.Database.CurrentTransaction == null)
        {
            // BeginTransactionAsync never got as far as creating one -- most often because the
            // cancellation token was already cancelled when the chain started.
            return;
        }

        try
        {
            // CancellationToken.None deliberately. A cancelled token is the single most likely reason
            // to be in here, and cancelling the rollback with it would leave the transaction open
            // until the DbContext is disposed.
            await dbContext.Database.RollbackTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A rollback that fails -- a dropped connection, a transaction the server already killed --
            // says nothing the caller can act on, and the exception being unwound is the one that
            // actually explains the failure. Losing it to a cleanup fault is the whole defect in
            // GH-4239, so this catch is deliberately silent and the original propagates from the
            // caller's `throw;`.
        }
    }
}
