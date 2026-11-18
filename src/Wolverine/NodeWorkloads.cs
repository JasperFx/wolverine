namespace Wolverine;

/// <summary>
///     GH-5601. The work one node of a Balanced Wolverine application takes on. Every node of the application runs
///     the same code against the same message store; only this setting differs, so one build can be deployed as an
///     HTTP tier, a messaging tier and a durability tier. The default is <see cref="All" />.
/// </summary>
/// <remarks>
///     Set it with <see cref="DurabilitySettings.Workloads" />, or one of the presets
///     <see cref="DurabilitySettings.RunAsHttpOnlyNode" />, <see cref="DurabilitySettings.RunAsMessagingNode" /> and
///     <see cref="DurabilitySettings.RunAsDurabilityNode" />. A node with no workloads still serves HTTP requests,
///     invokes handlers inline, runs its local queues and publishes through the outbox.
/// </remarks>
[Flags]
public enum NodeWorkloads
{
    /// <summary>
    ///     Serves HTTP (or gRPC) and publishes through the outbox. No application listeners, no agents, no scheduled
    ///     message polling, and never the leader
    /// </summary>
    None = 0,

    /// <summary>
    ///     Application listeners, the listener agent families (exclusive, leader-pinned, dynamic and transport
    ///     supplied listeners), scheduled message polling and recurring schedules. Only a node with Messaging may
    ///     lead the cluster
    /// </summary>
    Messaging = 1,

    /// <summary>
    ///     Eligible for the durability agents (<c>wolverinedb://</c>): inbox and outbox recovery, tenant database
    ///     scheduled messages, dead letter and handled envelope expiry, and node record sweeps. Requires
    ///     <see cref="Messaging" />, because inbox recovery runs the recovered envelopes through this node's own
    ///     listeners
    /// </summary>
    Durability = 2,

    /// <summary>
    ///     Eligible for the event subscription agents (<c>event-subscriptions://</c>): async projections and
    ///     subscriptions run as Wolverine-managed agents
    /// </summary>
    Projections = 4,

    /// <summary>
    ///     Everything. The default
    /// </summary>
    All = Messaging | Durability | Projections
}
