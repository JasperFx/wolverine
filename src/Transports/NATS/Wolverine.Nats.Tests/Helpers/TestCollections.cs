using Xunit;

namespace Wolverine.Nats.Tests.Helpers;

[CollectionDefinition("NATS Integration Tests")]
public class NatsIntegrationTestCollection : ICollectionFixture<NatsContainerFixture>
{
}

[CollectionDefinition("NATS MultiTenancy Tests")]
public class NatsMultiTenancyTestCollection : ICollectionFixture<NatsContainerFixture>
{
}

[CollectionDefinition("NATS Compliance", DisableParallelization = true)]
public class NatsComplianceTestCollection : ICollectionFixture<NatsContainerFixture>
{
}
