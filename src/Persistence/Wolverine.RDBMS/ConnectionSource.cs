using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
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
            if (context.IsDefaultTenant())
            {
                if (tenantedStore.Main is IConnectionSource<T> s2) return s2.CreateConnection();
            }
            else
            {
                var store = await tenantedStore.Source.FindAsync(context.TenantId!);
                if (store is IConnectionSource<T> s3) return s3.CreateConnection();
            }
        }

        throw new InvalidOperationException(
            $"Cannot create a DbConnection of type {typeof(T).FullNameInCode()} from message store {messageStore.GetType().FullNameInCode()}");
    }
}

public class ConnectionFrame<T> : MethodCall where T : DbConnection
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects ConnectionSource<T>.GetMethod(nameof(Create)) at codegen time. The Create method is statically referenced via nameof and the closed-generic ConnectionSource<T> is rooted at codegen time per the AOT guide.")]
    public ConnectionFrame() : base(typeof(ConnectionSource<T>), nameof(ConnectionSource<DbConnection>.Create))
    {
    }
}