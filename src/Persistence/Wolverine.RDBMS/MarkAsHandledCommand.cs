namespace Wolverine.RDBMS;

/// <summary>
/// GH-4705. The mark-incoming-as-handled statement together with, for drivers that bind parameters by
/// POSITION, the argument values in the exact order their placeholders appear in <see cref="Sql" />.
/// </summary>
/// <remarks>
/// Marten's <c>QueueSqlCommand</c> and its Polecat / Fisher equivalents take a raw statement and a flat
/// argument array, so a caller that composed the statement itself also had to know the argument order.
/// Under inbox partitioning that order is not obvious -- the statement is two statements, the envelope id
/// appears three times, and the destination appears two or three times depending on
/// <see cref="Wolverine.Persistence.Durability.MessageIdentity" /> -- which is exactly the kind of thing
/// three separate stores will get wrong independently. Callers that bind by NAME pass their own
/// placeholders and ignore <see cref="Arguments" />.
/// </remarks>
public sealed record MarkAsHandledCommand(string Sql, object[] Arguments)
{
    private MarkAsHandledCommand[]? _statements;

    /// <summary>
    /// The same work as one command per SQL statement, each with its own arguments in placeholder order. Under
    /// inbox partitioning <see cref="Sql" /> is two statements joined by <c>;</c>, which a batch that takes a
    /// single statement per command cannot accept -- Marten's <c>QueueSqlCommand</c> throws on the separator.
    /// Such a caller queues each of these, in order, instead. A single-statement command is its own only
    /// statement.
    /// </summary>
    public MarkAsHandledCommand[] Statements => _statements ?? [this];

    /// <summary>
    /// Several single statements as one command: <see cref="Sql" /> joined by <c>;</c> and
    /// <see cref="Arguments" /> concatenated, for callers that execute the text themselves, with the parts kept
    /// in <see cref="Statements" /> for callers that cannot.
    /// </summary>
    internal static MarkAsHandledCommand Sequence(params MarkAsHandledCommand[] statements)
    {
        return new MarkAsHandledCommand(
            string.Join(";", statements.Select(x => x.Sql)),
            statements.SelectMany(x => x.Arguments).ToArray())
        {
            _statements = statements
        };
    }
}
