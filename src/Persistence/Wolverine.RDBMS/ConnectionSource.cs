using System.Data.Common;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RDBMS;

public static class ConnectionSource<T> where T : DbConnection
{
    public static async ValueTask<T> Create(IMessageStore messageStore, MessageContext context) 
    {
        if (messageStore is IConnectionSource<T> source) return source.CreateConnection();

        if (messageStore is MultiTenantedMessageStore tenantedStore)
        {
            // TODO -- create a IsTenanted() method
            if (context.TenantId.IsEmpty() || context.TenantId == StorageConstants.DefaultTenantId)
            {
                if (tenantedStore.Main is IConnectionSource<T> s2) return s2.CreateConnection();
            }
            else
            {
                var store = await tenantedStore.Source.FindAsync(context.TenantId);
                if (store is IConnectionSource<T> s3) return s3.CreateConnection();
            }
        }

        throw new InvalidOperationException(
            $"Cannot create a DbConnection of type {typeof(T).FullNameInCode()} from message store {messageStore.GetType().FullNameInCode()}");
    }
}

public class ConnectionFrame<T> : MethodCall where T : DbConnection
{
    public ConnectionFrame() : base(typeof(ConnectionSource<T>), nameof(ConnectionSource<DbConnection>.Create))
    {
    }
}