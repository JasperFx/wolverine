using System.Data.Common;
using Marten.Internal;
using Marten.Internal.Operations;
using Marten.Services;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Storage;
using Wolverine.RDBMS;

namespace Wolverine.Marten.Persistence.Operations;

/// <summary>
/// GH-4505. Writes a logical deduplication claim as part of the Marten session's own unit of work, so
/// the claim commits with the handler's events and documents — and rolls back with them.
///
/// <para>
/// This is the whole point of the issue. <see cref="Wolverine.Persistence.Durability.IDeduplicationStore" />
/// claims through <c>DbDataSource.CreateCommand()</c> on a fresh connection, so its claim survives the
/// handler's rollback intact and has to be given back by a compensating DELETE. Queued here instead,
/// there is nothing to compensate for: a chain that throws never commits, and a chain that stops with a
/// <c>ProblemDetails</c> never reaches <c>SaveChangesAsync</c>, so neither one ever claimed.
/// </para>
///
/// <para>
/// Modelled on <see cref="StoreIncomingEnvelope" />, which has queued Wolverine's own inbox rows onto the
/// Marten session since the outbox was written. Same shape, same reason.
/// </para>
/// </summary>
internal class ClaimDeduplicationId : global::Marten.Internal.Operations.IStorageOperation, NoDataReturnedCall
{
    private readonly string _table;
    private readonly string _deduplicationId;
    private readonly DateTimeOffset _expires;

    public ClaimDeduplicationId(string table, string deduplicationId, DateTimeOffset expires)
    {
        _table = table;
        _deduplicationId = deduplicationId;
        _expires = expires;
    }

    public void ConfigureCommand(Weasel.Postgresql.ICommandBuilder builder, IStorageSession session)
    {
        builder.Append(
            $"insert into {_table} ({DatabaseConstants.DeduplicationId}, {DatabaseConstants.Expires}) values (");
        builder.AppendParameter(_deduplicationId);
        builder.Append(',');
        builder.AppendParameter(_expires);
        builder.Append(");");
    }

    public void Postprocess(DbDataReader reader, IList<Exception> exceptions)
    {
        // Nothing
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public OperationRole Role()
    {
        return OperationRole.Other;
    }

    /// <summary>
    /// <see cref="Envelope" />, exactly as <see cref="StoreIncomingEnvelope" /> reports, and for a reason
    /// worth stating: Marten runs <c>ensureStorageExistsAsync</c> over every queued operation's document
    /// type, so naming a type Marten has no mapping for fails the commit with
    /// "Could not determine an 'id/Id' field or property". This operation writes a Wolverine table that
    /// Marten does not own, and <c>Envelope</c> is the registered document type that stands for
    /// "Wolverine's own storage" on a Marten-integrated store.
    /// </summary>
    public Type DocumentType => typeof(Envelope);
}
