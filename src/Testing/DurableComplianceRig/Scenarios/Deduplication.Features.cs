using Bobcat;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Wolverine;

namespace DurableComplianceRig.Scenarios;

[BobcatFeature("Logical deduplication: PostgreSQL over a durable local queue, CompareByHash")]
public class dedup_postgresql_local_hash(ITestOutputHelper output)
    : DeduplicationCompliance(new PostgresqlStore(), new LocalQueueTransport(), MessageDeduplicationMode.CompareByHash, output);

[BobcatFeature("Logical deduplication: PostgreSQL over a durable local queue, CompareByString")]
public class dedup_postgresql_local_string(ITestOutputHelper output)
    : DeduplicationCompliance(new PostgresqlStore(), new LocalQueueTransport(), MessageDeduplicationMode.CompareByString, output);

[BobcatFeature("Logical deduplication: PostgreSQL over RabbitMQ, CompareByHash")]
public class dedup_postgresql_rabbitmq_hash(ITestOutputHelper output)
    : DeduplicationCompliance(new PostgresqlStore(), new RabbitMqTransport(), MessageDeduplicationMode.CompareByHash, output);

[BobcatFeature("Logical deduplication: PostgreSQL over RabbitMQ, CompareByString")]
public class dedup_postgresql_rabbitmq_string(ITestOutputHelper output)
    : DeduplicationCompliance(new PostgresqlStore(), new RabbitMqTransport(), MessageDeduplicationMode.CompareByString, output);

[BobcatFeature("Logical deduplication: SQL Server over a durable local queue, CompareByHash")]
public class dedup_sqlserver_local_hash(ITestOutputHelper output)
    : DeduplicationCompliance(new SqlServerStore(), new LocalQueueTransport(), MessageDeduplicationMode.CompareByHash, output);

[BobcatFeature("Logical deduplication: SQL Server over a durable local queue, CompareByString")]
public class dedup_sqlserver_local_string(ITestOutputHelper output)
    : DeduplicationCompliance(new SqlServerStore(), new LocalQueueTransport(), MessageDeduplicationMode.CompareByString, output);

[BobcatFeature("Logical deduplication: SQL Server over RabbitMQ, CompareByHash")]
public class dedup_sqlserver_rabbitmq_hash(ITestOutputHelper output)
    : DeduplicationCompliance(new SqlServerStore(), new RabbitMqTransport(), MessageDeduplicationMode.CompareByHash, output);

[BobcatFeature("Logical deduplication: SQL Server over RabbitMQ, CompareByString")]
public class dedup_sqlserver_rabbitmq_string(ITestOutputHelper output)
    : DeduplicationCompliance(new SqlServerStore(), new RabbitMqTransport(), MessageDeduplicationMode.CompareByString, output);

[BobcatFeature("Logical deduplication: Marten over a durable local queue, CompareByHash")]
public class dedup_marten_local_hash(ITestOutputHelper output)
    : DeduplicationCompliance(new MartenStore(), new LocalQueueTransport(), MessageDeduplicationMode.CompareByHash, output);

[BobcatFeature("Logical deduplication: Marten over a durable local queue, CompareByString")]
public class dedup_marten_local_string(ITestOutputHelper output)
    : DeduplicationCompliance(new MartenStore(), new LocalQueueTransport(), MessageDeduplicationMode.CompareByString, output);

[BobcatFeature("Logical deduplication: Marten over RabbitMQ, CompareByHash")]
public class dedup_marten_rabbitmq_hash(ITestOutputHelper output)
    : DeduplicationCompliance(new MartenStore(), new RabbitMqTransport(), MessageDeduplicationMode.CompareByHash, output);

[BobcatFeature("Logical deduplication: Marten over RabbitMQ, CompareByString")]
public class dedup_marten_rabbitmq_string(ITestOutputHelper output)
    : DeduplicationCompliance(new MartenStore(), new RabbitMqTransport(), MessageDeduplicationMode.CompareByString, output);

[BobcatFeature("Logical deduplication: EF Core on SQL Server over a durable local queue, CompareByHash")]
public class dedup_efcore_local_hash(ITestOutputHelper output)
    : DeduplicationCompliance(new EfCoreStore(), new LocalQueueTransport(), MessageDeduplicationMode.CompareByHash, output);

[BobcatFeature("Logical deduplication: EF Core on SQL Server over a durable local queue, CompareByString")]
public class dedup_efcore_local_string(ITestOutputHelper output)
    : DeduplicationCompliance(new EfCoreStore(), new LocalQueueTransport(), MessageDeduplicationMode.CompareByString, output);

[BobcatFeature("Logical deduplication: EF Core on SQL Server over RabbitMQ, CompareByHash")]
public class dedup_efcore_rabbitmq_hash(ITestOutputHelper output)
    : DeduplicationCompliance(new EfCoreStore(), new RabbitMqTransport(), MessageDeduplicationMode.CompareByHash, output);

[BobcatFeature("Logical deduplication: EF Core on SQL Server over RabbitMQ, CompareByString")]
public class dedup_efcore_rabbitmq_string(ITestOutputHelper output)
    : DeduplicationCompliance(new EfCoreStore(), new RabbitMqTransport(), MessageDeduplicationMode.CompareByString, output);
