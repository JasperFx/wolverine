using Xunit;

namespace Wolverine.Grpc.Tests;

/// <summary>
///     Tests that set <c>DynamicCodeBuilder.WithinCodegenCommand = true</c> (a static flag) must
///     not run in parallel with tests that start real Wolverine hosts (e.g., transport compliance
///     tests). When the flag is set during another test's host startup,
///     <c>applyMetadataOnlyModeIfDetected()</c> forces <c>DurabilityMode.MediatorOnly</c>,
///     breaking transport operations like <c>SendAsync</c> and <c>RequireResponse</c>.
///     Placing all such tests in this collection serialises them.
/// </summary>
[CollectionDefinition(Name)]
public class GrpcSerialTestsCollection : ICollectionFixture<object>
{
    public const string Name = "GrpcSerialTests";
}
