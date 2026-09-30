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
public sealed record MarkAsHandledCommand(string Sql, object[] Arguments);
