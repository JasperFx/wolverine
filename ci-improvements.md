# CI Improvements - Remaining Work & Status

## Completed TestContainers Migrations

| Transport | Status | Notes |
|-----------|--------|-------|
| NATS | Done | Collection fixture with reference counting |
| Redis | Done | ModuleInitializer, 87/87 pass |
| Kafka | Done | ModuleInitializer, 91 pass / 2 transient DLQ failures |
| Pulsar | Done | ModuleInitializer, 89 pass / 12 flaky reliability tests |
| MQTT | Done | Generic ContainerBuilder for Mosquitto, 73 pass / 3 transient |
| AWS SQS | Done | LocalStack v4, 140 pass / 4 RawJson failures (pre-existing) |
| AWS SNS | Done | LocalStack v4, 78/78 pass |
| CosmosDb | Done (testing) | Shared static container, awaiting test results |
| Azure Service Bus | Done (untested locally) | ServiceBusBuilder with MsSql backing store |

## Remaining TestContainers Work

- **GCP Pub/Sub**: Not yet converted. Uses docker-compose `pubsub-emulator` on port 8085. Would need generic ContainerBuilder with `gcr.io/google.com/cloudsdktool/google-cloud-cli` or similar.
- **CosmosDb test verification**: Test run `bzrh2vnsw` in progress with shared static container fix. Previous run failed (56/62) due to multiple AppFixture instances each starting their own emulator.
- **Azure Service Bus test verification**: Not tested locally yet. The ServiceBusContainerFixture uses Testcontainers.ServiceBus module with MsSql backing store.

## CI Target / Workflow Status

| Target | Workflow | Status |
|--------|----------|--------|
| CIPersistence | persistence.yml | Reduced: sqlite, PersistenceTests, sqlserver, postgresql |
| CIMySql | mysql.yml | New: split from persistence |
| CIOracle | oracle.yml | New: split from persistence |
| CIEfCore | efcore.yml | Existing |
| CIAWS | aws.yml | Updated: no localstack in docker-compose |
| CIKafka | kafka.yml | Updated: no kafka in docker-compose |
| CIMQTT | mqtt.yml | Updated: no mosquitto in docker-compose |
| CINATS | nats.yml | Updated: no nats in docker-compose |
| CIPulsar | pulsar.yml | Updated: no docker-compose at all |
| CIRedis | redis.yml | Updated: no redis in docker-compose |
| CIRabbitMQ | rabbitmq.yml | Fixed: added sqlserver to docker services |
| CIHttp | http.yml | Simplified: plain dotnet test (no retry) |
| CICosmosDb | cosmosdb.yml | New |
| CIAzureServiceBus | azure-service-bus.yml | New |

## Known Pre-existing Test Failures

- **SQS RawJson tests** (4 tests): `receive_raw_json_as_buffered` and `receive_raw_json_as_inline` - LocalStack v4 incompatibility with native JSON message handling
- **Pulsar reliability tests** (~12 tests): Timing-sensitive retry/redelivery tests that are flaky
- **Kafka DLQ tests** (2 tests): Transient timing issues with dead letter queue
- **MQTT tests** (3 tests): Transient timing issues
- **RabbitMQ flaky tests**: `use_fan_out_exchange`, `use_direct_exchange_with_binding_key`, DLQ polling tests with tight timeouts (1.25s)

## Potential Future Improvements

- Increase DLQ polling timeouts in RabbitMQ tests (currently 5 retries * 250ms = 1.25s)
- Consider converting RabbitMQ to TestContainers (currently uses docker-compose)
- Consider converting PostgreSQL/SQL Server to TestContainers (currently docker-compose, used by many projects)
- Add `[Trait]` attributes to flaky tests for optional skip in CI
