using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.AmazonSqs;
using Wolverine.RabbitMQ;

namespace DurableComplianceRig.Infrastructure;

/// <summary>
/// Keeps parallel rig runs apart. <c>RIG_SUFFIX</c> (letters and digits) goes into every schema, queue and tenant
/// database name, so two runs never share a table or a broker queue. Empty by default.
/// </summary>
public static class RigEnvironment
{
    public static string Suffix { get; } =
        new string((Environment.GetEnvironmentVariable("RIG_SUFFIX") ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static string SchemaSuffix => Suffix.Length == 0 ? "" : "_" + Suffix;
    public static string QueueSuffix => Suffix.Length == 0 ? "" : "-" + Suffix;
}

/// <summary>What a store promises about a handler's effect and its inbox row.</summary>
public enum EffectSemantics
{
    /// <summary>
    /// The inbox row is marked handled inside the handler's own transaction (Marten, EF Core with
    /// <c>AutoApplyTransactions</c>), so a message's effect commits exactly once even across a crash.
    /// </summary>
    ExactlyOnce,

    /// <summary>
    /// The inbox row is marked handled after the handler's transaction commits (the plain RDBMS stores), so a
    /// crash or a release between the two legitimately runs the message again. A duplicate is only a defect
    /// when nothing crashed and nothing was released.
    /// </summary>
    AtLeastOnce
}

/// <summary>A message store, the handlers that write the ledger through it, and the databases to read back.</summary>
public abstract class StoreProfile
{
    public abstract string Name { get; }

    /// <summary>Short and lowercase; part of every schema and queue name the profile creates.</summary>
    public abstract string Key { get; }

    public abstract EffectSemantics Semantics { get; }

    /// <summary>
    /// Persistence, transactional middleware, and the rig handlers for this store. Turns on the store's
    /// database-backed queue transport too when <paramref name="withDatabaseQueues" />.
    /// </summary>
    public abstract void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues);

    /// <summary>Route to and listen to this store's database-backed queue; the transport is already enabled.</summary>
    public abstract void ConfigureDatabaseQueue(WolverineOptions opts, string queueName);

    /// <summary>Every database a run writes to: one, or one per tenant.</summary>
    public abstract IReadOnlyList<RigDatabase> Databases { get; }

    public virtual async Task EnsureLedgerAsync()
    {
        foreach (var database in Databases) await database.EnsureLedgerAsync();
    }

    public async Task<RigSnapshot> SnapshotAsync()
    {
        var snapshot = new RigSnapshot { Executions = Script.Log };
        foreach (var database in Databases) await database.ReadIntoAsync(snapshot);
        return snapshot;
    }

    public override string ToString() => Name;
}

/// <summary>Where the rig's messages travel between being sent and being received.</summary>
public abstract class TransportProfile
{
    public abstract string Name { get; }
    public abstract string Key { get; }

    /// <summary>Route <see cref="RigMessage" /> and <see cref="RigItem" /> to <paramref name="queueName" /> and listen to it durably.</summary>
    public abstract void Configure(WolverineOptions opts, StoreProfile store, string queueName);

    public override string ToString() => Name;
}

public class LocalQueueTransport : TransportProfile
{
    public override string Name => "a durable local queue";
    public override string Key => "local";

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        opts.LocalQueue(queueName).UseDurableInbox().CustomizeOutgoing(RigIds.Stamp);
        opts.PublishMessage<RigMessage>().ToLocalQueue(queueName);
        opts.PublishMessage<RigItem>().ToLocalQueue(queueName);
    }
}

public class DatabaseQueueTransport : TransportProfile
{
    public override string Name => "a database-backed queue";
    public override string Key => "dbq";

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        store.ConfigureDatabaseQueue(opts, queueName.Replace('-', '_'));
    }
}

public class RabbitMqTransport : TransportProfile
{
    public override string Name => "RabbitMQ";
    public override string Key => "rabbit";

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();
        opts.PublishMessage<RigMessage>().ToRabbitQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.PublishMessage<RigItem>().ToRabbitQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.ListenToRabbitQueue(queueName).UseDurableInbox();
    }
}

public class SqsTransport : TransportProfile
{
    public override string Name => "Amazon SQS";
    public override string Key => "sqs";

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        opts.UseAmazonSqsTransportLocally().AutoProvision().DisableAllNativeDeadLetterQueues();
        opts.PublishMessage<RigMessage>().ToSqsQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.PublishMessage<RigItem>().ToSqsQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.ListenToSqsQueue(queueName).UseDurableInbox();
    }
}

/// <summary>
/// The envelope carrying a rig message gets the message's own id. Inbox, dead letter and outbox rows are keyed by
/// envelope id and the ledger by message id; without this the checker could not match a row to its message, and the
/// row-level invariants would pass without ever finding anything to judge.
/// </summary>
public static class RigIds
{
    public static void Stamp(Envelope envelope)
    {
        envelope.Id = envelope.Message switch
        {
            RigMessage message => message.Id,
            RigItem item => item.Id,
            _ => envelope.Id
        };
    }
}

/// <summary>The name a node writes into the ledger, so the checker can say which node ran what.</summary>
public record RigNodeName(string Name)
{
    public override string ToString() => Name;
}

public static class RigServiceCollectionExtensions
{
    public static void AddRigNode(this IServiceCollection services, string name) =>
        services.AddSingleton(new RigNodeName(name));
}
