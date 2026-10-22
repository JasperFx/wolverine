using Bobcat;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;

namespace DurableComplianceRig.Scenarios;

// Topology (a): items sent to a durable local queue, batched and executed on that same queue
[BobcatFeature("Durable batching: PostgreSQL, batches on a durable local queue")]
public class batching_postgresql_local(ITestOutputHelper output)
    : BatchingCompliance(new PostgresqlStore(), new LocalQueueTransport(), true, output);

// Topology (b): items arrive on a durable broker listener and are batched on the DEFAULT, buffered, batch queue
[BobcatFeature("Durable batching: PostgreSQL over RabbitMQ, batched on the default buffered queue")]
public class batching_postgresql_rabbitmq(ITestOutputHelper output)
    : BatchingCompliance(new PostgresqlStore(), new RabbitMqTransport(), false, output);

[BobcatFeature("Durable batching: PostgreSQL over Amazon SQS, batched on the default buffered queue")]
public class batching_postgresql_sqs(ITestOutputHelper output)
    : BatchingCompliance(new PostgresqlStore(), new SqsTransport(), false, output);

// Topology (b) with the batch queue made durable as well: members owned at the listener, carriers on a durable queue
[BobcatFeature("Durable batching: PostgreSQL over RabbitMQ, batched on a durable queue")]
public class batching_postgresql_rabbitmq_durable_batch_queue(ITestOutputHelper output)
    : BatchingCompliance(new PostgresqlStore(), new RabbitMqTransport(), true, output);
