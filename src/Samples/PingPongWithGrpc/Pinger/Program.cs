using Grpc.Net.Client;
using PingPongWithGrpc.Messages;
using ProtoBuf.Grpc.Client;

// Allow the client to talk HTTP/2 over plain HTTP for the local sample. The
// Ponger listens on http://localhost:5001 without TLS so the sample runs
// without a trusted dev cert.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

using var channel = GrpcChannel.ForAddress("http://localhost:5001");

// protobuf-net.Grpc synthesises the client from the [ServiceContract] interface —
// no generated stubs required in the code-first style.
var pingService = channel.CreateGrpcService<IPingService>();

Console.WriteLine("Pinging the Ponger every second. Ctrl+C to exit.");

for (var i = 0; ; i++)
{
    try
    {
        var reply = await pingService.Ping(new PingRequest { Message = $"ping {i}" });
        Console.WriteLine($"  <- {reply.Echo} (handled count: {reply.HandledCount})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  !! {ex.GetType().Name}: {ex.Message}");
    }

    await Task.Delay(TimeSpan.FromSeconds(1));
}
