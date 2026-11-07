namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-5435. A message store whose agent <see cref="IMessageStore.Uri" /> leaves out the server port, and can carry
/// it when that matters. Relational stores build <c>wolverinedb://{engine}/{host}/{database}/{schema}</c>, so two
/// tenant databases with one name on one host but different ports (two clusters or two containers on one box)
/// would otherwise share one Uri, and the second one would never get a durability agent of its own.
/// </summary>
internal interface IPortQualifiedAgentUri
{
    /// <summary>The server port, or null when the store cannot tell</summary>
    int? AgentUriPort { get; }

    /// <summary>Switch <see cref="IMessageStore.Uri" /> to the form that carries the port. Idempotent</summary>
    void UsePortQualifiedUri();
}
