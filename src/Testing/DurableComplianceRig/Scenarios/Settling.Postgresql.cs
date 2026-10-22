using Bobcat;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;

namespace DurableComplianceRig.Scenarios;

[BobcatFeature("Durable settling: PostgreSQL over a durable local queue")]
public class settling_postgresql_local(ITestOutputHelper output)
    : SettlingCompliance(new PostgresqlStore(), new LocalQueueTransport(), output);

[BobcatFeature("Durable settling: PostgreSQL over a PostgreSQL queue")]
public class settling_postgresql_database_queue(ITestOutputHelper output)
    : SettlingCompliance(new PostgresqlStore(), new DatabaseQueueTransport(), output);

[BobcatFeature("Durable settling: PostgreSQL over RabbitMQ")]
public class settling_postgresql_rabbitmq(ITestOutputHelper output)
    : SettlingCompliance(new PostgresqlStore(), new RabbitMqTransport(), output);

[BobcatFeature("Durable settling: PostgreSQL over Amazon SQS")]
public class settling_postgresql_sqs(ITestOutputHelper output)
    : SettlingCompliance(new PostgresqlStore(), new SqsTransport(), output);
