namespace Wolverine.RDBMS;

/// <summary>
/// GH-4736. One statement of the mark-incoming-as-handled command, together with -- for drivers that bind
/// parameters by POSITION -- the argument values in the exact order their placeholders appear in
/// <see cref="Sql" />. The <c>Sql</c> here carries no trailing <c>;</c>.
/// </summary>
public sealed record MarkAsHandledStatement(string Sql, object[] Arguments);

/// <summary>
/// GH-4705. The mark-incoming-as-handled command in a form the stores that queue it into somebody
/// <i>else's</i> batch can use -- Marten's <c>QueueSqlCommand</c>, Polecat's and Fisher's
/// <c>ITransactionParticipant</c>.
/// </summary>
/// <remarks>
/// <para>
/// Those callers take a raw statement and a flat argument array, so a caller that composed the statement
/// itself also had to know the argument order. Under inbox partitioning that order is not obvious -- the
/// command is two statements, the envelope id appears three times, and the destination appears two or three
/// times depending on <see cref="Wolverine.Persistence.Durability.MessageIdentity" /> -- which is exactly
/// the kind of thing three separate stores will get wrong independently. Callers that bind by NAME pass
/// their own placeholders and ignore the arguments.
/// </para>
/// <para>
/// GH-4736: and the command is exposed as <see cref="Statements" /> rather than only as one <c>;</c>-joined
/// string, because not every batch a caller queues into accepts more than one statement at a time. Marten's
/// <c>QueueSqlCommand</c> explicitly rejects any SQL containing a <c>;</c>, so the partitioned shape could
/// never pass through it: every durable-inbox message whose handler committed through a Marten session was
/// dead-lettered. A caller that can run several statements on one command -- anything going straight to an
/// ADO <c>CommandText</c> -- can keep using <see cref="Sql" /> and <see cref="Arguments" />.
/// </para>
/// </remarks>
public sealed record MarkAsHandledCommand
{
    public MarkAsHandledCommand(string sql, object[] arguments)
        : this(new MarkAsHandledStatement(sql, arguments))
    {
    }

    public MarkAsHandledCommand(params MarkAsHandledStatement[] statements)
    {
        if (statements.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(statements),
                "A mark-as-handled command needs at least one statement");
        }

        Statements = statements;

        // Composed once, here, rather than per access: this is built for every durable-inbox message.
        Sql = statements.Length == 1
            ? statements[0].Sql
            : string.Join(";", statements.Select(x => x.Sql));

        Arguments = statements.Length == 1
            ? statements[0].Arguments
            : statements.SelectMany(x => x.Arguments).ToArray();
    }

    /// <summary>
    /// The statements in the order they must run. Under inbox partitioning the order matters: the DELETE
    /// retires the incoming row when a Handled row for the identity already exists, and the UPDATE that
    /// follows is what flips status when it does not.
    /// </summary>
    public IReadOnlyList<MarkAsHandledStatement> Statements { get; }

    /// <summary>
    /// Every statement joined by <c>;</c>, for a caller executing the whole command on a single ADO command.
    /// Use <see cref="Statements" /> instead when the batch being queued into takes one statement at a time.
    /// </summary>
    public string Sql { get; }

    /// <summary>
    /// The arguments for <see cref="Sql" />, in placeholder order across all statements. Only meaningful to a
    /// caller that binds by POSITION.
    /// </summary>
    public object[] Arguments { get; }
}
