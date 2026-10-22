using Bobcat;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;

namespace DurableComplianceRig.Scenarios;

[BobcatFeature("Durable settling: SQL Server over a durable local queue")]
public class settling_sqlserver_local(ITestOutputHelper output)
    : SettlingCompliance(new SqlServerStore(), new LocalQueueTransport(), output);

[BobcatFeature("Durable settling: SQL Server over a SQL Server queue")]
public class settling_sqlserver_database_queue(ITestOutputHelper output)
    : SettlingCompliance(new SqlServerStore(), new DatabaseQueueTransport(), output);

[BobcatFeature("Durable settling: SQL Server over RabbitMQ")]
public class settling_sqlserver_rabbitmq(ITestOutputHelper output)
    : SettlingCompliance(new SqlServerStore(), new RabbitMqTransport(), output);

[BobcatFeature("Durable settling: SQL Server over Amazon SQS")]
public class settling_sqlserver_sqs(ITestOutputHelper output)
    : SettlingCompliance(new SqlServerStore(), new SqsTransport(), output);
