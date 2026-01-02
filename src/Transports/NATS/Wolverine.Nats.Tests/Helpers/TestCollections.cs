using Xunit;

namespace Wolverine.Nats.Tests.Helpers;

[CollectionDefinition("NATS Integration Tests")]
public class NatsIntegrationTestCollection : ICollectionFixture<object>
{
}

[CollectionDefinition("NATS MultiTenancy Tests")]
public class NatsMultiTenancyTestCollection : ICollectionFixture<object>
{
}

[CollectionDefinition("NATS Compliance", DisableParallelization = true)]
public class NatsComplianceTestCollection : ICollectionFixture<object>
{
}