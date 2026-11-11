namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// True once <see cref="RestoreV6Defaults"/> has been called. Extension packages
    /// (Wolverine.Http, the transports, the message stores) whose 7.0 default flips
    /// live outside <see cref="WolverineOptions"/> read this flag to decide which
    /// default to apply.
    ///
    /// <para><b>Contract for anything that flips a default in 7.x:</b> users call
    /// <see cref="RestoreV6Defaults"/> at the top of <c>UseWolverine</c>, usually before
    /// <c>UseRabbitMq()</c>, <c>AddWolverineHttp()</c> or a message store is configured.
    /// So read this flag when the default is resolved (at startup or compile time, or
    /// through a nullable "not set by the user" value), never in a constructor or at
    /// the moment the extension is registered. An explicit setting by the user always
    /// wins over both the 7.0 default and the restored 6.x value.</para>
    /// </summary>
    public bool V6DefaultsRestored { get; private set; }

    /// <summary>
    /// Restore every runtime default that changed between Wolverine 6.x and 7.0 back
    /// to its Wolverine 6.x value. Use this on upgrade when you want Wolverine 7's API
    /// surface, target frameworks and bug fixes without also taking its new runtime
    /// defaults, then opt into each new default at your own pace. It is meant as a
    /// temporary migration step.
    ///
    /// <para>Call it once, near the top of your <c>UseWolverine</c> lambda:</para>
    /// <code>
    ///   builder.Host.UseWolverine(opts =>
    ///   {
    ///       opts.RestoreV6Defaults();
    ///       // … the rest of your configuration
    ///   });
    /// </code>
    ///
    /// <para>Settings you make explicitly, before or after this call, always win.</para>
    ///
    /// <para>This replaces <c>RestoreV5Defaults()</c>, which was removed in 7.0. 6.0's
    /// <see cref="ServiceLocationPolicy.NotAllowed"/> default is the 6.x value, so this
    /// method leaves it alone.</para>
    ///
    /// <para>Every default flipped in 7.x must be restored here (or, for a setting in
    /// an extension package, honour <see cref="V6DefaultsRestored"/>) and must get a
    /// row in the 7.0 defaults table of the
    /// <see href="https://wolverinefx.net/guide/migration.html">migration guide</see>.
    /// <c>CoreTests.restore_v6_defaults_tests</c> cross-checks that table.</para>
    /// </summary>
    public void RestoreV6Defaults()
    {
        V6DefaultsRestored = true;

        // GH-5194: no deduplication tables (MessageDeduplicationMode.None) and no deduplicated responses
        Durability.RestoreV6DeduplicationDefaults();

        // GH-5193: 7.0 always creates the inbox/outbox timestamp columns, the outbox stale index and
        // wolverine_nodes.load_factor. Restored as the 6.x layout, each column only exists when its setting is
        // on and the index is not created. Resolved through the flag so an explicit setting still wins.
        Durability.V6DefaultsRestored = true;

        // GH-5197: 7.0 creates envelope ids with EnvelopeIdGeneration.GuidV7, 6.x with NewId. Nothing to assign:
        // the EnvelopeIdGeneration getter reads V6DefaultsRestored, so an explicit setting still wins and a SQL
        // Server-only application still switches to NewIdSqlServerOrder at startup, as it did in 6.x.

        // GH-5200: three more core defaults read the flags set above when they are resolved, so an explicit setting
        // still wins: Durability.CompressAgentCommands (on in 7.0), GroupByGroupId() for a batch whose element type
        // is in a partitioned topology (resolved at startup) and the in-memory idempotency guard on NativeAck
        // endpoints (resolved in Endpoint.Compile()).

        // GH-5201: three transport defaults read V6DefaultsRestored when the transport starts: SQS
        // ExtendVisibilityWhileHandling, Kafka idempotent producers and RabbitMQ DrainWaitForPrefetch.

        // GH-5139: 7.0 identifies an inbox row by envelope id and destination (MessageIdentity.IdAndDestination),
        // 6.x by envelope id alone. Nothing to assign: the MessageIdentity getter reads Durability.V6DefaultsRestored,
        // set above, so an explicit setting still wins.

        // GH-5192 / GH-5511: new database queue tables get deterministic ordering, and SQL Server's throughput layout,
        // in 7.0. Each database transport (PostgreSQL, SQL Server, MySQL, Oracle, SQLite) reads V6DefaultsRestored when
        // it starts, before its first queue table is built, so an explicit setting still wins and an existing table is
        // never rebuilt by the default either way.

        // GH-5519: four durability defaults read Durability.V6DefaultsRestored, set above, when they are resolved, so an
        // explicit setting still wins:
        // - Durability.AlwaysMakeScheduledMessagesDurable (on in 7.0; changes nothing without a message store)
        // - Durability.EnableInboxPartitioning (on in 7.0, honoured by PostgreSQL only; other stores need it set explicitly)
        // - Durability.DeadLetterQueueExpirationEnabled (on in 7.0)
        // - Durability.InboxStaleTime / OutboxStaleTime (1 hour in 7.0 outside ServerlessWithOutbox, null in 6.x)

        // Add one line here for each core default 7.x flips, a row to the 7.0 defaults
        // table in docs/guide/migration.md and an entry to restore_v6_defaults_tests.
        // Defaults owned by extension packages read V6DefaultsRestored instead.
    }
}
