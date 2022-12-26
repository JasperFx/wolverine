using System.Net;
using System.Net.Sockets;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.Transports.Tcp;

public class SocketSenderProtocol : ISenderProtocol
{
    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        if (batch.Data.Length == 0)
        {
            throw new Exception("No data to be sent");
        }

        using var client = new TcpClient();
        var connection = connectAsync(client, batch.Destination)
            .TimeoutAfterAsync(5000);

        await connection;

        if (connection.IsCompleted)
        {
            await using var stream = client.GetStream();
            var protocolTimeout = WireProtocol.SendAsync(stream, batch, batch.Data, callback);
            //var protocolTimeout = .TimeoutAfter(5000);
            await protocolTimeout.ConfigureAwait(false);

            if (!protocolTimeout.IsCompleted)
            {
                await callback.MarkTimedOutAsync(batch);
            }

            if (protocolTimeout.IsFaulted)
            {
                await callback.MarkProcessingFailureAsync(batch, protocolTimeout.Exception);
            }
        }
        else
        {
            await callback.MarkTimedOutAsync(batch);
        }
    }

    private Task connectAsync(TcpClient client, Uri destination)
    {
        return string.Equals(Dns.GetHostName(), destination.Host, StringComparison.OrdinalIgnoreCase)
               || destination.Host == "localhost"
               || destination.Host == "127.0.0.1"
            ? client.ConnectAsync(IPAddress.Loopback, destination.Port)
            : client.ConnectAsync(destination.Host, destination.Port);
    }
}