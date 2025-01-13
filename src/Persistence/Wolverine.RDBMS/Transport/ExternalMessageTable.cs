using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.RDBMS.Transport;

public class ExternalMessageTable : Endpoint, IExternalMessageTable
{
    public ExternalMessageTable(DbObjectName tableName) : base(new Uri($"{ExternalDbTransport.ProtocolName}://{tableName.QualifiedName.ToLowerInvariant()}"), EndpointRole.Application)
    {
        IsListener = true;
        TableName = tableName;
        Mode = EndpointMode.Durable;
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode == EndpointMode.Durable;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var database = runtime.Storage as IMessageDatabase;
        if (database == null)
        {
            throw new InvalidOperationException(
                "The external table transport option can only be used in combination with a relational database message storage option, but the message store is " +
                runtime.Storage);
        }

        return new ValueTask<IListener>(new ExternalMessageTableListener(this, runtime, receiver));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException();
    }

    public TimeSpan PollingInterval { get; set; } = 10.Seconds();

    public DbObjectName TableName { get; init; }
    public string IdColumnName { get; set; } = "id";
    public string JsonBodyColumnName { get; set; } = "body";
    public string MessageTypeColumnName { get; set; } = null;
    public string TimestampColumnName { get; set; } = "timestamp";

    public bool AllowWolverineControl { get; set; } = true;

    public int AdvisoryLock { get; set; } = 12000;

    public IEnumerable<string> Columns()
    {
        yield return IdColumnName;
        yield return JsonBodyColumnName;

        if (MessageTypeColumnName.IsNotEmpty())
        {
            yield return MessageTypeColumnName;
        }
    }

    public async Task<Envelope[]> ReadAllAsync(DbDataReader reader, CancellationToken token)
    {
        var envelopes = new List<Envelope>();

        while (await reader.ReadAsync(token))
        {
            var id = await reader.GetFieldValueAsync<Guid>(0, token);
            var json = await reader.GetFieldValueAsync<byte[]>(1, token);

            var envelope = new Envelope
            {
                Id = id,
                Data = json,
                Destination = Uri
            };

            if (MessageTypeColumnName.IsEmpty())
            {
                envelope.MessageType = MessageType?.ToMessageTypeName();
            }
            else
            {
                var messageTypeName = await reader.GetFieldValueAsync<string>(2, token);
                envelope.MessageType = messageTypeName.IsEmpty() ? MessageType?.ToMessageTypeName() : messageTypeName;
            }
            
            envelopes.Add(envelope);
        }


        return envelopes.ToArray();
    }


}