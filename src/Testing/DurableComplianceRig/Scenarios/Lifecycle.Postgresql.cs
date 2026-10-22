using Bobcat;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;

namespace DurableComplianceRig.Scenarios;

[BobcatFeature("Node lifecycle: PostgreSQL over a durable local queue")]
public class lifecycle_postgresql_local(ITestOutputHelper output)
    : NodeLifecycleCompliance(new KillablePostgresqlStore(), new LocalQueueTransport(), output);

[BobcatFeature("Node lifecycle: PostgreSQL over RabbitMQ")]
public class lifecycle_postgresql_rabbitmq(ITestOutputHelper output)
    : NodeLifecycleCompliance(new KillablePostgresqlStore(), new RabbitMqTransport(), output);

[BobcatFeature("Node lifecycle: PostgreSQL over a PostgreSQL queue")]
public class lifecycle_postgresql_database_queue(ITestOutputHelper output)
    : NodeLifecycleCompliance(new KillablePostgresqlStore(), new DatabaseQueueTransport(), output);

[BobcatFeature("Node lifecycle: PostgreSQL over Amazon SQS")]
public class lifecycle_postgresql_sqs(ITestOutputHelper output)
    : NodeLifecycleCompliance(new KillablePostgresqlStore(), new SqsTransport(), output);

[BobcatFeature("Listener disruption: PostgreSQL over a durable local queue")]
public class disruption_postgresql_local(ITestOutputHelper output)
    : ListenerDisruptionCompliance(new KillablePostgresqlStore(), new LocalQueueTransport(), output);

[BobcatFeature("Listener disruption: PostgreSQL over RabbitMQ")]
public class disruption_postgresql_rabbitmq(ITestOutputHelper output)
    : ListenerDisruptionCompliance(new KillablePostgresqlStore(), new RabbitMqTransport(), output);

[BobcatFeature("Listener disruption: PostgreSQL over a PostgreSQL queue")]
public class disruption_postgresql_database_queue(ITestOutputHelper output)
    : ListenerDisruptionCompliance(new KillablePostgresqlStore(), new DatabaseQueueTransport(), output);

[BobcatFeature("Listener disruption: PostgreSQL over Amazon SQS")]
public class disruption_postgresql_sqs(ITestOutputHelper output)
    : ListenerDisruptionCompliance(new KillablePostgresqlStore(), new SqsTransport(), output);
