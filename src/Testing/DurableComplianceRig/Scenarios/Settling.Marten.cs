using Bobcat;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;

namespace DurableComplianceRig.Scenarios;

[BobcatFeature("Durable settling: Marten over a durable local queue")]
public class settling_marten_local(ITestOutputHelper output)
    : SettlingCompliance(new MartenStore(), new LocalQueueTransport(), output);

[BobcatFeature("Durable settling: Marten over a PostgreSQL queue")]
public class settling_marten_database_queue(ITestOutputHelper output)
    : SettlingCompliance(new MartenStore(), new DatabaseQueueTransport(), output);

[BobcatFeature("Durable settling: Marten over RabbitMQ")]
public class settling_marten_rabbitmq(ITestOutputHelper output)
    : SettlingCompliance(new MartenStore(), new RabbitMqTransport(), output);

[BobcatFeature("Durable settling: Marten over Amazon SQS")]
public class settling_marten_sqs(ITestOutputHelper output)
    : SettlingCompliance(new MartenStore(), new SqsTransport(), output);
