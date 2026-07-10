using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Wolverine.Http.Tests.EfCoreOnly;

public record Bug3358CreateItem(string Name);

public record Bug3358ItemStored(Guid Id);

public static class Bug3358DbContextCascadeEndpoint
{
    // Injects the DbContext and cascades ONLY through the return tuple - never IMessageBus or
    // IMessageContext. HttpChain.RequiresOutbox() only reflects an injected bus dependency, so this
    // shape can never satisfy it: before GH-3358 the RequiresOutbox() gate on the GH-3291 branch of
    // EFCorePersistenceFrameProvider.ApplyTransactionSupport(chain, container) skipped the outbox
    // enrollment and the cascaded Bug3358ItemStored was dispatched before SaveChangesAsync committed.
    // The DbContext dependency makes CanApply true, so AutoApplyTransactions routes this chain to the
    // two-argument overload - the third of the three endpoint shapes in the GH-3291/GH-3353 family.
    [WolverinePost("/bug3358/dbcontext-cascade")]
    public static (IResult, Bug3358ItemStored) Post(Bug3358CreateItem command, Bug3353DbContext db)
    {
        var item = new Bug3353Item { Id = Guid.NewGuid(), Name = command.Name };
        db.Items.Add(item);
        return (Results.Ok(), new Bug3358ItemStored(item.Id));
    }
}
