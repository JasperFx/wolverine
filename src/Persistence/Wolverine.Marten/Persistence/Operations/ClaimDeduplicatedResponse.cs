using System.Data.Common;
using Marten.Internal;
using Marten.Internal.Operations;
using Marten.Services;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Storage;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace Wolverine.Marten.Persistence.Operations;

/// <summary>
/// GH-5160. Writes a <c>[DeduplicatedWithResponse]</c> claim and its response as one row, as part of the Marten session's
/// own unit of work, so the claim commits with the endpoint's events and documents and rolls back with them.
///
/// <para>
/// The <c>wolverine_deduplicated_responses</c> twin of <see cref="ClaimDeduplicationId" />. There is no unanswered
/// claim to release afterwards and no response to record after the commit: both are in this INSERT.
/// </para>
/// </summary>
internal class ClaimDeduplicatedResponse : global::Marten.Internal.Operations.IStorageOperation, NoDataReturnedCall
{
    private readonly string _table;
    private readonly PendingDeduplicatedResponseClaim _claim;

    public ClaimDeduplicatedResponse(string table, PendingDeduplicatedResponseClaim claim)
    {
        _table = table;
        _claim = claim;
    }

    public void ConfigureCommand(Weasel.Postgresql.ICommandBuilder builder, IStorageSession session)
    {
        builder.Append(
            $"insert into {_table} ({DatabaseConstants.DeduplicationId}, {DatabaseConstants.Expires}, {DatabaseConstants.Fingerprint}, {DatabaseConstants.ClaimToken}, {DatabaseConstants.ResponseStatusCode}, {DatabaseConstants.ResponseBody}, {DatabaseConstants.ResponseLocation}) values (");
        builder.AppendParameter(_claim.DeduplicationId);
        builder.Append(',');
        builder.AppendParameter(_claim.Expires);
        builder.Append(',');
        builder.AppendParameter(_claim.Fingerprint);
        builder.Append(',');
        builder.AppendParameter(_claim.ClaimToken);
        builder.Append(',');

        // Typed, so a null still binds: an unanswered claim has no status, body or location.
        var response = _claim.Response;
        builder.AppendParameter(response == null ? DBNull.Value : (object)response.StatusCode, NpgsqlDbType.Integer);
        builder.Append(',');
        builder.AppendParameter((object?)response?.Body ?? DBNull.Value, NpgsqlDbType.Text);
        builder.Append(',');
        builder.AppendParameter((object?)response?.Location ?? DBNull.Value, NpgsqlDbType.Text);
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
    /// <see cref="Envelope" />, for the reason <see cref="ClaimDeduplicationId.DocumentType" /> gives: Marten
    /// ensures storage for every queued operation's document type, and this writes a table Marten does not own.
    /// </summary>
    public Type DocumentType => typeof(Envelope);
}
