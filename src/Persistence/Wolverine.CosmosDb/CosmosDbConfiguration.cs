namespace Wolverine.CosmosDb;

/// <summary>
///     Optional configuration for Wolverine's CosmosDB integration, supplied through
///     <see cref="WolverineCosmosDbExtensions.UseCosmosDbPersistence(WolverineOptions,string,Action{CosmosDbConfiguration})" />.
/// </summary>
public class CosmosDbConfiguration
{
    /// <summary>
    ///     Is each saga document stored in its own logical partition, keyed by the saga id? False by default.
    ///     See <see cref="PartitionSagasById" />.
    /// </summary>
    public bool SagasArePartitionedById { get; private set; }

    /// <summary>
    ///     GH-3415. Store every saga document in its own logical partition, keyed by the saga id, instead of
    ///     leaving them all in CosmosDB's single "undefined" partition.
    ///     <para>
    ///         A saga is a user's own POCO, so nothing writes the container's <c>/partitionKey</c> property into
    ///         it, and a document with no partition key value lands in the undefined partition. Wolverine reads
    ///         and writes sagas there consistently, so it works — but a logical partition is capped at 20 GB and
    ///         10,000 RU/s, and that cap is then the ceiling for the application's entire saga workload no matter
    ///         how far the container is scaled out. The symptom is 429 throttling that does not improve when you
    ///         add RU/s, because the limit is per logical partition, not per container.
    ///     </para>
    ///     <para>
    ///         With this on, Wolverine stamps <c>partitionKey</c> = the saga id onto the document as it writes it,
    ///         and reads, updates and deletes the saga as a single-partition point operation — the access pattern
    ///         CosmosDB is at its best on. Sagas then spread across every physical partition the container has.
    ///     </para>
    ///     <para>
    ///         This changes where a saga document lives, so it is opt in: saga documents written before it was
    ///         turned on stay in the undefined partition, where a point read keyed on the saga id will not find
    ///         them. Turn it on for a new application, or migrate existing saga documents first — see the
    ///         "Saga Partitioning" section of the CosmosDB documentation.
    ///     </para>
    /// </summary>
    public CosmosDbConfiguration PartitionSagasById()
    {
        SagasArePartitionedById = true;
        return this;
    }
}
