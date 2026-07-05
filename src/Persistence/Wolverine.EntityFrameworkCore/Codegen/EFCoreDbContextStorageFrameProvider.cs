using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Wolverine.Persistence;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// Teaches the provider-agnostic <see cref="StorageAttribute"/> (<c>[Storage(typeof(MyDbContext))]</c>)
/// how to designate the transactional DbContext for an EF Core handler. This is the EF Core sibling of
/// <see cref="Wolverine.Marten.MartenAncillaryStoreFrameProvider"/> — its only job is to claim
/// DbContext-shaped store types so <c>[Storage]</c> resolves instead of throwing "no integration owns
/// this store".
/// <para>
/// The actual selection is made by <c>EFCorePersistenceFrameProvider.DetermineDbContextType(IChain, ...)</c>,
/// which reads <c>chain.AncillaryStoreType</c> (set by <c>[Storage]</c>) and enrolls that DbContext
/// through the ordinary EF Core transaction middleware. There is therefore no separate ancillary
/// outbox session to build — unlike Marten/Polecat, a designated DbContext is resolved from the
/// container the same way as any other — so the inserted frame is intentionally a no-op marker.
/// </para>
/// </summary>
internal class EFCoreDbContextStorageFrameProvider : IAncillaryStoreFrameProvider
{
    private readonly EFCorePersistenceFrameProvider _provider;

    public EFCoreDbContextStorageFrameProvider(EFCorePersistenceFrameProvider provider)
    {
        _provider = provider;
    }

    public bool Matches(Type storeType)
        => storeType.CanBeCastTo<DbContext>() || _provider.IsRegisteredAbstraction(storeType);

    public Frame BuildOutboxFactoryFrame(Type storeType)
        => new CommentFrame(
            $"[Storage(typeof({storeType.FullNameInCode()}))] designates the transactional DbContext; enrollment is handled by the EF Core transaction middleware.");
}
