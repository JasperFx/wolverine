using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Test collection for NATS integration tests that require a running NATS server.
/// These tests will be skipped if no NATS server is available at localhost:4222.
/// </summary>
[CollectionDefinition("NATS Integration Tests", DisableParallelization = true)]
public class NatsIntegrationTestCollection
{
    // This class has no code, and is never instantiated. 
    // Its purpose is just to be the place to apply [CollectionDefinition] 
    // and all the attributes that configure the collection.
}