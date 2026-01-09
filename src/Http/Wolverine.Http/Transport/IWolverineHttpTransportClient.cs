using System.Text.Json;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

public interface IWolverineHttpTransportClient
{
    Task SendBatchAsync(string uri, OutgoingMessageBatch batch);
    Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions serializerOptions);
}