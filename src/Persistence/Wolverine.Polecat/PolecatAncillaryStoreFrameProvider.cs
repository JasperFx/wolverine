using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Polecat;
using Wolverine.Persistence;
using Wolverine.Polecat.Codegen;

namespace Wolverine.Polecat;

/// <summary>
/// Teaches the generic <see cref="StorageAttribute"/> (<c>[Storage(typeof(IMyStore))]</c>) how to
/// route a handler to a Polecat ancillary store. Registered when Polecat is integrated with Wolverine.
/// </summary>
internal class PolecatAncillaryStoreFrameProvider : IAncillaryStoreFrameProvider
{
    public bool Matches(Type storeType) => storeType.CanBeCastTo<IDocumentStore>();

    public Frame BuildOutboxFactoryFrame(Type storeType) => new AncillaryOutboxFactoryFrame(storeType);
}
