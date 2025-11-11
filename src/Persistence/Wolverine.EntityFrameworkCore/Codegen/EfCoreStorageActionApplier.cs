using Microsoft.EntityFrameworkCore;
using Wolverine.Persistence;

namespace Wolverine.EntityFrameworkCore.Codegen;

public static class EfCoreStorageActionApplier
{
    public static async Task ApplyAction<TEntity, TDbContext>(TDbContext context, IStorageAction<TEntity> action) where TDbContext : DbContext
    {
        if (action.Entity == null) return;
        
        switch (action.Action)
        {
            case StorageAction.Delete:
                context.Remove(action.Entity);
                break;
            case StorageAction.Insert:
                await context.AddAsync(action.Entity);
                break;
            case StorageAction.Store:
                context.Update(action.Entity); // Not really correct, but let it go
                break;
            case StorageAction.Update:
                context.Update(action.Entity);
                break;
                
        }
    }
}