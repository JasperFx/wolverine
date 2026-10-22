using Bobcat;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;

namespace DurableComplianceRig.Scenarios;

[BobcatFeature("Durable settling: EF Core on SQL Server over a durable local queue")]
public class settling_efcore_local(ITestOutputHelper output)
    : SettlingCompliance(new EfCoreStore(), new LocalQueueTransport(), output);

[BobcatFeature("Durable settling: EF Core on SQL Server over a SQL Server queue")]
public class settling_efcore_database_queue(ITestOutputHelper output)
    : SettlingCompliance(new EfCoreStore(), new DatabaseQueueTransport(), output);

[BobcatFeature("Durable settling: EF Core on SQL Server over RabbitMQ")]
public class settling_efcore_rabbitmq(ITestOutputHelper output)
    : SettlingCompliance(new EfCoreStore(), new RabbitMqTransport(), output);

[BobcatFeature("Durable settling: EF Core on SQL Server over Amazon SQS")]
public class settling_efcore_sqs(ITestOutputHelper output)
    : SettlingCompliance(new EfCoreStore(), new SqsTransport(), output);
