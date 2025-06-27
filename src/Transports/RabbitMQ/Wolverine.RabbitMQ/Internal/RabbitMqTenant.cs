using JasperFx.Core;
using RabbitMQ.Client;
using Wolverine.Runtime;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqTenant
{
    public RabbitMqTenant(string tenantId, string virtualHostName)
    {
        TenantId = tenantId;
        VirtualHostName = virtualHostName ?? throw new ArgumentNullException(nameof(virtualHostName));
        Transport = new RabbitMqTransport();
    }

    public RabbitMqTenant(string tenantId, RabbitMqTransport transport)
    {
        TenantId = tenantId;
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public string TenantId { get; }
    public RabbitMqTransport Transport { get; private set; }
    public string? VirtualHostName { get; set; }

    public RabbitMqTransport Compile(RabbitMqTransport parent)
    {
        if (VirtualHostName.IsNotEmpty())
        {
            var props = typeof(ConnectionFactory).GetProperties();

            Transport.ConfigureFactory(f =>
            {
                foreach (var prop in props)
                {
                    if (!prop.CanWrite) continue;

                    prop.SetValue(f, prop.GetValue(parent.ConnectionFactory));
                }

                f.VirtualHost = VirtualHostName;
            });
        }

        CloneDeadLetterQueue(parent);

        return Transport!;
    }

    private void CloneDeadLetterQueue(RabbitMqTransport parent)
    {
        // Copy the parent dead letter queue configuration to the tenant
        Transport.DeadLetterQueue.Mode = parent.DeadLetterQueue.Mode;
        Transport.DeadLetterQueue.QueueName = parent.DeadLetterQueue.QueueName;
        Transport.DeadLetterQueue.ExchangeName = parent.DeadLetterQueue.ExchangeName;
        Transport.DeadLetterQueue.BindingName = parent.DeadLetterQueue.BindingName;
        Transport.DeadLetterQueue.ConfigureQueue = parent.DeadLetterQueue.ConfigureQueue;
        Transport.DeadLetterQueue.ConfigureExchange = parent.DeadLetterQueue.ConfigureExchange;
    }

    public Task ConnectAsync(RabbitMqTransport parent, IWolverineRuntime runtime)
    {
        Compile(parent);
        return Transport.ConnectAsync(runtime).AsTask();
    }
}
