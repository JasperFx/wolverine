using Wolverine;
using Wolverine.RabbitMQ;

namespace DurableComplianceRig.Infrastructure;

/// <summary>
/// Endpoints with no durable inbox. Handler idempotency (<c>AutoApplyTransactions(IdempotencyStyle)</c>) is
/// documented as applying only to these: a durable endpoint already deduplicates by the inbox's primary key.
/// </summary>
public class BufferedLocalQueueTransport : TransportProfile
{
    public override string Name => "a buffered local queue";
    public override string Key => "local-buffered";

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        opts.LocalQueue(queueName).BufferedInMemory().CustomizeOutgoing(RigIds.Stamp);
        opts.PublishMessage<RigMessage>().ToLocalQueue(queueName);
        opts.PublishMessage<RigItem>().ToLocalQueue(queueName);
    }
}

public class InlineRabbitMqTransport : TransportProfile
{
    public override string Name => "an inline RabbitMQ listener";
    public override string Key => "rabbit-inline";

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();
        opts.PublishMessage<RigMessage>().ToRabbitQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.PublishMessage<RigItem>().ToRabbitQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.ListenToRabbitQueue(queueName).ProcessInline();
    }
}

public class BufferedRabbitMqTransport : TransportProfile
{
    public override string Name => "a buffered RabbitMQ listener";
    public override string Key => "rabbit-buffered";

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();
        opts.PublishMessage<RigMessage>().ToRabbitQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.PublishMessage<RigItem>().ToRabbitQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.ListenToRabbitQueue(queueName).BufferedInMemory();
    }
}
