namespace Wolverine.Runtime.Agents;

/// <summary>
/// The single width that every message store's agent, node and listener URI columns are held to.
/// </summary>
/// <remarks>
/// GH-4246 / GH-4280. These columns -- <c>wolverine_agent_restrictions.uri</c>,
/// <c>wolverine_node_assignments.id</c>, <c>wolverine_nodes.uri</c> and <c>wolverine_listeners.uri</c> --
/// all hold a URI that Wolverine itself composes, and two of them are primary keys. Providers used to pick
/// their own width for each one, and a column declared with a bare <c>AddColumn&lt;string&gt;()</c> quietly
/// took whatever Weasel's default string mapping was for that engine: <c>varchar(100)</c> on SQL Server,
/// <c>VARCHAR(255)</c> on MySQL. An <c>event-subscriptions://marten/SomeProjection@some-tenant-id</c> or a
/// <c>wolverinedb://sqlserver/host/schema/database</c> runs past both without trying, and the failure lands
/// as a truncation error on a live write path rather than at startup.
///
/// The shared compliance suite proves every store can round-trip a URI of exactly this length, so a
/// provider that declares a narrower column now fails a test instead of failing in production. 500 is what
/// the node-table family already used across SQL Server, MySQL and Oracle; anything in an InnoDB index key
/// has to stay under 768 characters of utf8mb4, which is the ceiling on raising it.
/// </remarks>
public static class AgentUri
{
    public const int MaximumLength = 500;
}
